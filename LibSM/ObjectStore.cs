using System.IO.Compression;
using System.Text;

namespace SourceManager;

public class ObjectStore
{
    private readonly string _objectsPath;
    private readonly Repository _repo;

    public ObjectStore(Repository repo)
    {
        _repo = repo;
        _objectsPath = Path.Combine(repo.SmPath, "objects");
    }

    public Hash WriteObject(ObjectType type, byte[] data)
    {
        Hash hash = type switch
        {
            ObjectType.Blob => HashUtil.ComputeBlob(data),
            ObjectType.Tree => HashUtil.ComputeTree(data),
            ObjectType.Commit => HashUtil.ComputeCommit(data),
            ObjectType.Tag => HashUtil.ComputeTag(data),
            _ => throw new ArgumentException($"Unknown object type: {type}")
        };
        WritePayload(hash, type, data);
        return hash;
    }

    /// <summary>
    /// Stores bytes that are ALREADY in on-disk form (framed and compressed). Prefer
    /// <see cref="WritePayload"/> for ordinary writes and <see cref="WriteObject"/> when the
    /// caller wants the hash computed too.
    /// </summary>
    public void WriteObjectRaw(Hash hash, byte[] storageBytes)
    {
        string hex = hash.ToHex();
        string dir = Path.Combine(_objectsPath, hex[..2]);
        string file = Path.Combine(dir, hex[2..]);
        if (File.Exists(file)) return;

        PathUtil.EnsureDirectory(dir);
        string tmp = file + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, storageBytes);
        File.Move(tmp, file, true);
    }

    /// <summary>
    /// Frames a payload as "&lt;type&gt; &lt;size&gt;\0&lt;payload&gt;". Storing the type with the object
    /// removes the need to infer it from the payload, which is what made a commit (whose body
    /// begins with "tree") indistinguishable from a tree.
    /// </summary>
    public static byte[] Frame(ObjectType type, byte[] payload)
    {
        string header = $"{TypeName(type)} {payload.Length}\0";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        byte[] framed = new byte[headerBytes.Length + payload.Length];
        Array.Copy(headerBytes, 0, framed, 0, headerBytes.Length);
        Array.Copy(payload, 0, framed, headerBytes.Length, payload.Length);
        return framed;
    }

    /// <summary>Compresses a payload into the on-disk loose-object representation.</summary>
    public static byte[] CompressFramed(ObjectType type, byte[] payload)
        => Compress(Frame(type, payload));

    public static string TypeName(ObjectType type) => type switch
    {
        ObjectType.Blob => "blob",
        ObjectType.Tree => "tree",
        ObjectType.Commit => "commit",
        ObjectType.Tag => "tag",
        _ => "blob"
    };

    /// <summary>
    /// Splits an on-disk loose object into its declared type and payload. Returns null when the
    /// payload is not self-describing (an object written by an older build), so callers can fall
    /// back to content detection for backward compatibility.
    /// </summary>
    private static (ObjectType type, byte[] payload)? Unframe(byte[] stored)
    {
        int nul = Array.IndexOf(stored, (byte)0);
        if (nul <= 0) return null;

        int space = Array.IndexOf(stored, (byte)' ', 0, nul);
        if (space <= 0) return null;

        string typeName = Encoding.ASCII.GetString(stored, 0, space);
        ObjectType type = typeName switch
        {
            "blob" => ObjectType.Blob,
            "tree" => ObjectType.Tree,
            "commit" => ObjectType.Commit,
            "tag" => ObjectType.Tag,
            _ => (ObjectType)255
        };
        if ((byte)type == 255) return null;

        if (!int.TryParse(Encoding.ASCII.GetString(stored, space + 1, nul - space - 1), out int size))
            return null;
        if (size != stored.Length - nul - 1) return null;

        byte[] payload = new byte[size];
        Array.Copy(stored, nul + 1, payload, 0, size);
        return (type, payload);
    }

    /// <summary>
    /// Decompresses the stored object exactly as written — including the "&lt;type&gt; &lt;size&gt;\0"
    /// storage header. Prefer <see cref="ReadPayload"/> or <see cref="ReadObjectWithType"/>
    /// unless the caller genuinely needs the stored framing.
    /// </summary>
    /// <summary>
    /// Stores a raw (uncompressed) payload, framing and compressing it. Use this when writing
    /// an object that arrived over the wire; <see cref="WriteObjectRaw"/> expects bytes that are
    /// already in storage form.
    /// </summary>
    public void WritePayload(Hash hash, ObjectType type, byte[] payload)
    {
        string hex = hash.ToHex();
        string dir = Path.Combine(_objectsPath, hex[..2]);
        string file = Path.Combine(dir, hex[2..]);
        if (File.Exists(file)) return;

        PathUtil.EnsureDirectory(dir);
        byte[] compressed = CompressFramed(type, payload);
        string tmp = file + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, compressed);
        File.Move(tmp, file, true);
    }

    public byte[]? ReadObject(Hash hash)
    {
        string hex = hash.ToHex();
        string file = Path.Combine(_objectsPath, hex[..2], hex[2..]);
        if (!File.Exists(file)) return null;
        byte[] compressed = File.ReadAllBytes(file);
        return Decompress(compressed);
    }

    public (ObjectType type, byte[] data)? ReadObjectWithType(Hash hash)
    {
        byte[]? stored = ReadObject(hash);
        if (stored == null) return null;

        var unframed = Unframe(stored);
        if (unframed.HasValue) return unframed.Value;

        // Legacy loose object written before the type was stored alongside the payload.
        return (DetectTypeLegacy(stored), stored);
    }

    public bool ObjectExists(Hash hash)
    {
        string hex = hash.ToHex();
        return File.Exists(Path.Combine(_objectsPath, hex[..2], hex[2..]));
    }

    public bool Exists(Hash hash) => ObjectExists(hash);

    public byte[]? ReadObjectRaw(Hash hash)
    {
        string hex = hash.ToHex();
        string file = Path.Combine(_objectsPath, hex[..2], hex[2..]);
        if (!File.Exists(file)) return null;
        return File.ReadAllBytes(file);
    }

    public string GetObjectPath(Hash hash)
    {
        string hex = hash.ToHex();
        return Path.Combine(_objectsPath, hex[..2], hex[2..]);
    }

    public Hash WriteBlob(byte[] content)
    {
        return WriteObject(ObjectType.Blob, content);
    }

    public Hash WriteBlobFromFile(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        return WriteBlob(content);
    }

    public byte[]? ReadBlob(Hash hash)
    {
        return ReadPayload(hash);
    }

    public Hash WriteTree(Tree tree)
    {
        byte[] data = tree.Serialize();
        return WriteObject(ObjectType.Tree, data);
    }

    public Tree? ReadTree(Hash hash)
    {
        byte[]? data = ReadPayload(hash);
        if (data == null) return null;
        return Tree.Deserialize(data);
    }

    public Hash WriteCommit(Commit commit)
    {
        byte[] data = commit.Serialize();
        return WriteObject(ObjectType.Commit, data);
    }

    public Commit? ReadCommit(Hash hash)
    {
        byte[]? data = ReadPayload(hash);
        if (data == null) return null;
        return Commit.Deserialize(data);
    }

    public Hash WriteTag(TagObject tag)
    {
        byte[] data = tag.Serialize();
        return WriteObject(ObjectType.Tag, data);
    }

    /// <summary>
    /// Reads a tag object. Returns null when the id does not name a tag — a lightweight tag
    /// ref points straight at a commit, and callers routinely pass such a ref here.
    /// </summary>
    public TagObject? ReadTag(Hash hash)
    {
        if (ReadObjectWithType(hash)?.type != ObjectType.Tag) return null;
        byte[]? data = ReadPayload(hash);
        if (data == null) return null;
        return TagObject.Deserialize(data);
    }

    /// <summary>
    /// Returns just the object's payload, transparently stripping the storage header.
    /// Legacy objects that predate the header are returned as-is.
    /// </summary>
    public byte[]? ReadPayload(Hash hash)
    {
        byte[]? stored = ReadObject(hash);
        if (stored == null) return null;
        var unframed = Unframe(stored);
        return unframed.HasValue ? unframed.Value.payload : stored;
    }

    public IEnumerable<Hash> EnumerateObjects()
    {
        if (!Directory.Exists(_objectsPath)) yield break;
        foreach (string dir in Directory.GetDirectories(_objectsPath))
        {
            string prefix = Path.GetFileName(dir);
            if (prefix.Length != 2) continue;
            foreach (string file in Directory.GetFiles(dir))
            {
                string suffix = Path.GetFileName(file);
                if (suffix.Length == 62)
                    yield return Hash.Parse(prefix + suffix);
            }
        }
    }

    public int ObjectCount()
    {
        if (!Directory.Exists(_objectsPath)) return 0;
        int count = 0;
        foreach (string dir in Directory.GetDirectories(_objectsPath))
        {
            count += Directory.GetFiles(dir).Length;
        }
        return count;
    }

    public long ObjectStorageSize()
    {
        if (!Directory.Exists(_objectsPath)) return 0;
        long total = 0;
        foreach (string dir in Directory.GetDirectories(_objectsPath))
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                total += new FileInfo(file).Length;
            }
        }
        return total;
    }

    public void PruneObject(Hash hash)
    {
        string hex = hash.ToHex();
        string file = Path.Combine(_objectsPath, hex[..2], hex[2..]);
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// Best-effort type inference for legacy loose objects that were stored WITHOUT a type
    /// header. New objects always carry their type, so this only runs when opening a repository
    /// written by an older build.
    /// </summary>
    private static ObjectType DetectTypeLegacy(byte[] data)
    {
        if (LooksLikeCommit(data)) return ObjectType.Commit;
        if (LooksLikeTag(data)) return ObjectType.Tag;
        if (LooksLikeTree(data)) return ObjectType.Tree;
        return ObjectType.Blob;
    }

    /// <summary>A commit starts with "tree &lt;64-hex&gt;\n" and carries author/committer headers.</summary>
    private static bool LooksLikeCommit(byte[] data)
    {
        if (!StartsWithAscii(data, "tree ")) return false;
        if (data.Length < 69) return false;
        for (int i = 5; i < 69; i++)
        {
            if (data[i] == '\n') return true;
            if (!IsHexDigit(data[i])) return false;
        }
        return false;
    }

    /// <summary>A tag starts "object &lt;64-hex&gt;\n" and carries a type header.</summary>
    private static bool LooksLikeTag(byte[] data)
    {
        if (!StartsWithAscii(data, "object ")) return false;
        if (data.Length < 71) return false;
        for (int i = 7; i < 71; i++)
        {
            if (data[i] == '\n') return true;
            if (!IsHexDigit(data[i])) return false;
        }
        return false;
    }

    /// <summary>
    /// A tree is a sequence of "&lt;mode&gt; &lt;name&gt;\0&lt;32 bytes&gt;" entries, where the mode is
    /// decimal digits followed by a space and no control characters precede the NUL.
    /// </summary>
    private static bool LooksLikeTree(byte[] data)
    {
        if (data.Length == 0) return false;
        int i = 0;
        while (i < data.Length)
        {
            int modeStart = i;
            while (i < data.Length && data[i] >= '0' && data[i] <= '9') i++;
            if (i == modeStart) return false;
            if (i >= data.Length || data[i] != ' ') return false;
            i++;
            int nameStart = i;
            while (i < data.Length && data[i] != 0)
            {
                if (data[i] < 32 && data[i] != '\t') return false;
                i++;
            }
            if (i >= data.Length || data[i] != 0) return false;
            if (i == nameStart) return false;
            i++;
            if (i + 32 > data.Length) return false;
            i += 32;
        }
        return true;
    }

    private static bool StartsWithAscii(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != (byte)prefix[i]) return false;
        }
        return true;
    }

    private static bool IsHexDigit(byte b)
        => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    // ─── Static deserialization helpers for Transport layer ───
    public static Commit? DeserializeCommit(byte[] data)
    {
        try { return Commit.Deserialize(data); }
        catch { return null; }
    }

    public static Tree? DeserializeTree(byte[] data)
    {
        try { return Tree.Deserialize(data); }
        catch { return null; }
    }

    public static TagObject? DeserializeTag(byte[] data)
    {
        try { return TagObject.Deserialize(data); }
        catch { return null; }
    }

    public static byte[]? DeserializeBlob(byte[] data)
    {
        return data;
    }

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}