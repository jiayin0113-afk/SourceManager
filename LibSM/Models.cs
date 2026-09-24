using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceManager;

/// <summary>
/// Serialization helpers. Object payloads must use a canonical LF-only encoding on every
/// platform: these payloads are hashed, so the line ending is part of the object identity.
/// Never use StringBuilder.AppendLine here — on Windows it emits CRLF and silently produces
/// objects that canonical parsers cannot read back.
/// </summary>
internal static class CanonicalText
{
    public static void AppendLine(StringBuilder sb, string text)
    {
        sb.Append(text).Append('\n');
    }

    /// <summary>Splits object payloads on LF, tolerating (and stripping) a stray CR.</summary>
    public static string[] SplitLines(string text)
    {
        string[] raw = text.Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i].Length > 0 && raw[i][^1] == '\r')
                raw[i] = raw[i][..^1];
        }
        return raw;
    }
}

public enum ObjectType : byte
{
    Blob = 0,
    Tree = 1,
    Commit = 2,
    Tag = 3,
    RefDelta = 4,
    OfsDelta = 5
}

public enum FileMode : ushort
{
    Normal = 33188,
    Executable = 33261,
    Symlink = 40960,
    Directory = 16384,
    Submodule = 57344
}

public static class FileModeFormat
{
    /// <summary>
    /// Renders a mode the conventional way (octal, e.g. "100644"), not as a raw ushort.
    /// Printing the decimal value ("033188") looks like octal and is simply wrong.
    /// </summary>
    public static string ToOctal(FileMode mode) => Convert.ToString((int)mode, 8).PadLeft(6, '0');

    /// <summary>Parses "100644" (octal) into a FileMode.</summary>
    public static FileMode FromOctal(string text) => (FileMode)Convert.ToInt32(text, 8);
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
    Ignored
}

public enum MergeStrategy
{
    FastForward,
    Recursive,
    Ours,
    Theirs,
    Octopus,
    Subtree,
    Resolve,
    Patience
}

public enum DiffAlgorithm
{
    Myers,
    Patience,
    Histogram,
    Minimal,
    Semantic
}

public enum ConflictResolution
{
    Unresolved,
    Ours,
    Theirs,
    Merged,
    Manual
}

// The interactive-rebase step model lives in RebasePlan.cs (RebaseOp + RebaseStep), where it is
// actually used. A never-referenced RebaseStep enum used to sit here, which is why `rebase -i`
// looked implemented while doing nothing.

public enum SignatureKind
{
    Author,
    Committer,
    Tagger
}

public enum BisectState
{
    Good,
    Bad,
    Skip,
    Unknown
}

public enum OperationType
{
    Commit,
    Checkout,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Reset,
    BranchCreate,
    BranchDelete,
    TagCreate,
    TagDelete,
    Stash,
    StashPop,
    RemoteAdd,
    RemoteRemove,
    Fetch,
    Push,
    Pull,
    Bisect,
    Amend
}

public record struct Hash(byte[] Bytes)
{
    public static readonly Hash Zero = new(new byte[32]);

    public static Hash Compute(byte[] data)
    {
        return new Hash(System.Security.Cryptography.SHA256.HashData(data));
    }

    public static Hash Compute(Stream stream)
    {
        return new Hash(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static Hash Parse(string hex)
    {
        if (hex.Length != 64)
            throw new ArgumentException($"Invalid hash length: {hex.Length}, expected 64 hex chars");
        byte[] bytes = new byte[32];
        for (int i = 0; i < 32; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return new Hash(bytes);
    }

    public static Hash? TryParse(string hex)
    {
        if (hex.Length != 64) return null;
        try { return Parse(hex); }
        catch { return null; }
    }

    public string Short => ToHex()[..7];

    /// <summary>
    /// Null-safe: a default(Hash) has a null byte array, and callers legitimately hold one
    /// before it is populated. Rendering it as all zeros keeps diagnostics working instead
    /// of turning every such path into a NullReferenceException.
    /// </summary>
    public string ToHex()
    {
        if (Bytes == null) return new string('0', 64);
        var sb = new StringBuilder(64);
        foreach (byte b in Bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public override string ToString() => ToHex();

    public string ToPath()
    {
        string hex = ToHex();
        return Path.Combine(hex[..2], hex[2..]);
    }

    public override int GetHashCode() => Bytes[0] | (Bytes[1] << 8) | (Bytes[2] << 16) | (Bytes[3] << 24);

    public bool Equals(Hash other) => Bytes.AsSpan().SequenceEqual(other.Bytes);
}

public record struct Signature
{
    public string Name { get; init; }
    public string Email { get; init; }
    public DateTimeOffset When { get; init; }
    public int TimezoneOffset { get; init; }

    public Signature(string name, string email, DateTimeOffset when)
    {
        Name = name;
        Email = email;
        When = when;
        TimezoneOffset = (int)when.Offset.TotalMinutes;
    }

    public static Signature Now(string name, string email)
        => new(name, email, DateTimeOffset.Now);

    public string Format()
    {
        int offset = TimezoneOffset;
        char sign = offset >= 0 ? '+' : '-';
        offset = Math.Abs(offset);
        int hours = offset / 60;
        int minutes = offset % 60;
        long unix = When.ToUnixTimeSeconds();
        return $"{Name} <{Email}> {unix} {sign}{hours:D2}{minutes:D2}";
    }

    public static Signature Parse(ReadOnlySpan<char> line)
    {
        int emailStart = line.IndexOf('<');
        int emailEnd = line.IndexOf('>');
        if (emailStart < 1 || emailEnd <= emailStart)
            throw new FormatException($"Malformed signature: '{line.ToString()}'");

        string name = line[..(emailStart - 1)].Trim().ToString();
        string email = line[(emailStart + 1)..emailEnd].Trim().ToString();
        ReadOnlySpan<char> rest = line[(emailEnd + 1)..].Trim();
        int space = rest.IndexOf(' ');
        if (space < 0 ||
            !long.TryParse(rest[..space], out long unix) ||
            rest.Length < space + 5)
        {
            // Timestamp is optional in practice; fall back to the epoch with a zero offset
            // rather than corrupting the whole object parse.
            return new Signature(name, email, DateTimeOffset.FromUnixTimeSeconds(0));
        }

        string tz = rest[(space + 1)..].ToString();
        int totalMinutes = 0;
        if (tz.Length >= 5 &&
            int.TryParse(tz.AsSpan(1, 2), out int tzHours) &&
            int.TryParse(tz.AsSpan(3, 2), out int tzMinutes))
        {
            totalMinutes = tzHours * 60 + tzMinutes;
            if (tz[0] == '-') totalMinutes = -totalMinutes;
        }
        return new Signature(name, email, DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(TimeSpan.FromMinutes(totalMinutes)));
    }
}

public enum DiffLineType
{
    Context,
    Added,
    Deleted
}

public record class DiffLine
{
    public DiffLineType Type { get; init; }
    public string Text { get; init; } = string.Empty;
    public int OldLineNumber { get; init; }
    public int NewLineNumber { get; init; }
}

public record class DiffHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    public string Header { get; init; } = string.Empty;
    public List<DiffLine> Lines { get; init; } = new();
}

public record class FileDiff
{
    public string OldPath { get; init; } = string.Empty;
    public string NewPath { get; init; } = string.Empty;
    public Hash? OldHash { get; init; }
    public Hash? NewHash { get; init; }
    public FileMode OldMode { get; init; }
    public FileMode NewMode { get; init; }
    public ChangeKind Kind { get; init; }
    public List<DiffHunk> Hunks { get; set; } = new();
    public bool IsBinary { get; set; }

    /// <summary>
    /// Content similarity in 0..1 for a detected rename/copy. 1.0 means identical content
    /// (a pure move); lower values indicate the file was also edited.
    /// </summary>
    public double Similarity { get; set; } = 1.0;
}

public record class StashEntry
{
    public Hash CommitHash { get; init; }
    public Hash? IndexHash { get; init; }
    public Hash? UntrackedHash { get; init; }
    public string Message { get; init; } = string.Empty;
    public Signature Author { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public record class ReflogEntry
{
    public Hash OldHash { get; init; }
    public Hash NewHash { get; init; }
    public Signature Author { get; set; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

public record class RemoteInfo
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public List<string> FetchRefSpecs { get; init; } = new();
    public List<string> PushRefSpecs { get; init; } = new();
    public string? PushUrl { get; init; }
    public Dictionary<string, string> Options { get; init; } = new();
}

public record class BranchInfo
{
    public string Name { get; init; } = string.Empty;
    public Hash TipHash { get; init; }
    public string? UpstreamBranch { get; init; }
    public string? RemoteName { get; init; }
    public bool IsHead { get; init; }

    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            name = Name,
            hash = TipHash.ToHex(),
            upstream = UpstreamBranch,
            remote = RemoteName,
            isHead = IsHead
        });

    public static BranchInfo FromJson(string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new BranchInfo
        {
            Name = root.GetProperty("name").GetString() ?? "",
            TipHash = Hash.Parse(root.GetProperty("hash").GetString() ?? ""),
            UpstreamBranch = root.TryGetProperty("upstream", out var u) ? u.GetString() : null,
            RemoteName = root.TryGetProperty("remote", out var r) ? r.GetString() : null,
            IsHead = root.TryGetProperty("isHead", out var ih) && ih.GetBoolean()
        };
    }
}

public record class WorkingTreeStatus
{
    /// <summary>
    /// Index vs HEAD: what a commit would record right now.
    /// </summary>
    public Dictionary<string, ChangeKind> Staged { get; init; } = new();

    /// <summary>
    /// Working tree vs index: edits that are NOT yet staged. A path may legitimately appear
    /// here and in <see cref="Staged"/> at the same time (partially staged file).
    /// </summary>
    public Dictionary<string, ChangeKind> Changes { get; init; } = new();

    public List<string> Untracked { get; init; } = new();
    public List<string> Conflicted { get; init; } = new();
    public string? CurrentBranch { get; init; }
    public string? UpstreamBranch { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>True when a commit right now would record something.</summary>
    public bool HasStagedChanges => Staged.Count > 0 || Conflicted.Count > 0;

    public bool IsClean => Staged.Count == 0 && Changes.Count == 0 &&
                           Untracked.Count == 0 && Conflicted.Count == 0;
}

public record class CommitGraphNode
{
    public Hash CommitHash { get; init; }
    public List<Hash> ParentHashes { get; init; } = new();
    public List<Hash> ChildHashes { get; init; } = new();
}

public record class TreeEntry
{
    public FileMode Mode { get; init; }
    public string Name { get; init; } = string.Empty;
    public Hash ObjectHash { get; init; }

    public byte[] Serialize()
    {
        string header = $"{(int)Mode:D6} {Name}\0";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] result = new byte[headerBytes.Length + 32];
        Array.Copy(headerBytes, 0, result, 0, headerBytes.Length);
        Array.Copy(ObjectHash.Bytes, 0, result, headerBytes.Length, 32);
        return result;
    }

    public static (TreeEntry, int) Deserialize(ReadOnlySpan<byte> data)
    {
        int nullPos = data.IndexOf((byte)0);
        ReadOnlySpan<byte> header = data[..nullPos];
        int spacePos = header.IndexOf((byte)' ');
        ushort mode = ushort.Parse(Encoding.UTF8.GetString(header[..spacePos]));
        string name = Encoding.UTF8.GetString(header[(spacePos + 1)..]);
        byte[] hashBytes = data.Slice(nullPos + 1, 32).ToArray();
        return (new TreeEntry
        {
            Mode = (FileMode)mode,
            Name = name,
            ObjectHash = new Hash(hashBytes)
        }, nullPos + 1 + 32);
    }
}

public record class Tree
{
    public List<TreeEntry> Entries { get; init; } = new();

    public byte[] Serialize()
    {
        var entries = Entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        int totalSize = entries.Sum(e => e.Serialize().Length);
        byte[] result = new byte[totalSize];
        int offset = 0;
        foreach (var entry in entries)
        {
            byte[] serialized = entry.Serialize();
            Array.Copy(serialized, 0, result, offset, serialized.Length);
            offset += serialized.Length;
        }
        return result;
    }

    public static Tree Deserialize(ReadOnlySpan<byte> data)
    {
        var entries = new List<TreeEntry>();
        int offset = 0;
        while (offset < data.Length)
        {
            var (entry, consumed) = TreeEntry.Deserialize(data[offset..]);
            entries.Add(entry);
            offset += consumed;
        }
        return new Tree { Entries = entries };
    }
}

public record class Commit
{
    public Hash? TreeHash { get; init; }
    public List<Hash> ParentHashes { get; init; } = new();
    public Signature Author { get; init; }
    public Signature Committer { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new();

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        CanonicalText.AppendLine(sb, $"tree {TreeHash?.ToHex()}");
        foreach (var parent in ParentHashes)
            CanonicalText.AppendLine(sb, $"parent {parent.ToHex()}");
        CanonicalText.AppendLine(sb, $"author {Author.Format()}");
        CanonicalText.AppendLine(sb, $"committer {Committer.Format()}");
        foreach (var h in Headers)
            CanonicalText.AppendLine(sb, $"{h.Key} {h.Value}");
        sb.Append('\n');
        sb.Append(Message);
        if (!Message.EndsWith('\n'))
            sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static Commit Deserialize(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        var headers = new Dictionary<string, string>();
        Hash? treeHash = null;
        var parents = new List<Hash>();
        Signature author = default, committer = default;
        string message = string.Empty;

        var lines = CanonicalText.SplitLines(text);
        int i = 0;
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                i++;
                break;
            }
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            string key = line[..space];
            string value = line[(space + 1)..];
            switch (key)
            {
                case "tree":
                    if (value.Length >= 64)
                        treeHash = Hash.Parse(value[..64]);
                    break;
                case "parent":
                    parents.Add(Hash.Parse(value[..64]));
                    break;
                case "author":
                    author = Signature.Parse(value.AsSpan());
                    break;
                case "committer":
                    committer = Signature.Parse(value.AsSpan());
                    break;
                default:
                    headers[key] = value;
                    break;
            }
        }

        message = string.Join('\n', lines[i..]);
        return new Commit
        {
            TreeHash = treeHash,
            ParentHashes = parents,
            Author = author,
            Committer = committer,
            Message = message,
            Headers = headers
        };
    }
}

public record class TagObject
{
    public Hash TargetHash { get; init; }
    public ObjectType TargetType { get; init; } = ObjectType.Commit;
    public string Name { get; init; } = string.Empty;
    public Signature Tagger { get; init; }
    public string Message { get; init; } = string.Empty;

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        CanonicalText.AppendLine(sb, $"object {TargetHash.ToHex()}");
        CanonicalText.AppendLine(sb, $"type {TargetType.ToString().ToLowerInvariant()}");
        CanonicalText.AppendLine(sb, $"tag {Name}");
        CanonicalText.AppendLine(sb, $"tagger {Tagger.Format()}");
        sb.Append('\n');
        sb.Append(Message);
        if (!Message.EndsWith('\n'))
            sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static TagObject Deserialize(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        var lines = CanonicalText.SplitLines(text);
        Hash targetHash = default;
        ObjectType targetType = ObjectType.Commit;
        string name = string.Empty;
        Signature tagger = default;
        string message = string.Empty;
        bool sawObjectHeader = false;
        int i = 0;
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrEmpty(line)) { i++; break; }
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            string key = line[..space];
            string value = line[(space + 1)..];
            switch (key)
            {
                case "object":
                    if (value.Length >= 64) targetHash = Hash.Parse(value[..64]);
                    sawObjectHeader = true;
                    break;
                case "type":
                    targetType = Enum.Parse<ObjectType>(value, true);
                    break;
                case "tag":
                    name = value;
                    break;
                case "tagger":
                    tagger = Signature.Parse(value.AsSpan());
                    break;
            }
        }

        // A tag object must carry an "object" header. Without this check a commit payload fed
        // to ReadTag silently deserializes into an empty tag whose TargetHash is null.
        if (!sawObjectHeader)
            throw new FormatException("Not a tag object: missing 'object' header");

        message = string.Join('\n', lines[i..]);
        return new TagObject
        {
            TargetHash = targetHash,
            TargetType = targetType,
            Name = name,
            Tagger = tagger,
            Message = message
        };
    }
}

public record class IndexEntry
{
    public DateTimeOffset CTime { get; set; }
    public DateTimeOffset MTime { get; set; }
    public uint Device { get; set; }
    public uint Inode { get; set; }
    public FileMode Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public long FileSize { get; set; }
    public Hash ObjectHash { get; set; }
    public string Path { get; set; } = string.Empty;
    public ushort Flags { get; set; }
    public bool AssumeUnchanged { get; set; }
    public bool IntentToAdd { get; set; }
    public bool SkipWorktree { get; set; }
    public int Stage { get; set; }

    public int PathLength => Path.Length;

    // On-disk entry layout (76 bytes of fixed fields, then the path, then NUL padding
    // to the next 8-byte boundary):
    //   0..4   ctime seconds        36..44  file size (int64)
    //   4..8   ctime nanoseconds    44..76  object hash (32)
    //   8..12  mtime seconds        76..78  flags (uint16)
    //  12..16  mtime nanoseconds    78..    path (UTF-8, no trailing NUL inside the entry)
    //  16..20  device               78+len  NUL padding
    //  20..24  inode
    //  24..28  mode
    //  28..32  uid
    //  32..36  gid
    public const int FixedFieldsSize = 78;

    public byte[] Serialize()
    {
        int pathLen = Encoding.UTF8.GetByteCount(Path);
        int unpadded = FixedFieldsSize + pathLen;
        int padding = (8 - (unpadded % 8)) % 8;
        byte[] buf = new byte[unpadded + padding];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (uint)CTime.ToUnixTimeSeconds());
        BitConverter.TryWriteBytes(buf.AsSpan(4), (uint)(CTime.ToUnixTimeMilliseconds() % 1000000000));
        BitConverter.TryWriteBytes(buf.AsSpan(8), (uint)MTime.ToUnixTimeSeconds());
        BitConverter.TryWriteBytes(buf.AsSpan(12), (uint)(MTime.ToUnixTimeMilliseconds() % 1000000000));
        BitConverter.TryWriteBytes(buf.AsSpan(16), Device);
        BitConverter.TryWriteBytes(buf.AsSpan(20), Inode);
        BitConverter.TryWriteBytes(buf.AsSpan(24), (uint)Mode);
        BitConverter.TryWriteBytes(buf.AsSpan(28), Uid);
        BitConverter.TryWriteBytes(buf.AsSpan(32), Gid);
        BitConverter.TryWriteBytes(buf.AsSpan(36), FileSize);
        Array.Copy(ObjectHash.Bytes, 0, buf, 44, 32);
        ushort flags = (ushort)(Math.Min(pathLen, 0xFFF) | (AssumeUnchanged ? 0x8000 : 0) | (IntentToAdd ? 0x4000 : 0) | (SkipWorktree ? 0x2000 : 0) | ((Stage & 3) << 12));
        BitConverter.TryWriteBytes(buf.AsSpan(76), flags);
        Encoding.UTF8.GetBytes(Path, buf.AsSpan(78));
        return buf;
    }

    public static (IndexEntry, int) Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < FixedFieldsSize)
            throw new InvalidDataException("Truncated index entry");

        int ctimeSec = BitConverter.ToInt32(data[..4]);
        int ctimeNano = BitConverter.ToInt32(data.Slice(4, 4));
        int mtimeSec = BitConverter.ToInt32(data.Slice(8, 4));
        int mtimeNano = BitConverter.ToInt32(data.Slice(12, 4));
        uint device = BitConverter.ToUInt32(data.Slice(16, 4));
        uint inode = BitConverter.ToUInt32(data.Slice(20, 4));
        uint mode = BitConverter.ToUInt32(data.Slice(24, 4));
        uint uid = BitConverter.ToUInt32(data.Slice(28, 4));
        uint gid = BitConverter.ToUInt32(data.Slice(32, 4));
        long fileSize = BitConverter.ToInt64(data.Slice(36, 8));
        byte[] hashBytes = data.Slice(44, 32).ToArray();
        ushort flags = BitConverter.ToUInt16(data.Slice(76, 2));
        int pathLen = flags & 0xFFF;
        if (pathLen > data.Length - FixedFieldsSize)
            pathLen = data.Length - FixedFieldsSize;
        string path = Encoding.UTF8.GetString(data.Slice(FixedFieldsSize, pathLen)).TrimEnd('\0');
        int unpadded = FixedFieldsSize + pathLen;
        int padding = (8 - (unpadded % 8)) % 8;
        int total = unpadded + padding;
        if (total > data.Length) total = data.Length;
        return (new IndexEntry
        {
            CTime = DateTimeOffset.FromUnixTimeSeconds(ctimeSec).AddTicks(ctimeNano / 100),
            MTime = DateTimeOffset.FromUnixTimeSeconds(mtimeSec).AddTicks(mtimeNano / 100),
            Device = device,
            Inode = inode,
            Mode = (FileMode)mode,
            Uid = uid,
            Gid = gid,
            FileSize = fileSize,
            ObjectHash = new Hash(hashBytes),
            Path = path,
            Flags = flags,
            AssumeUnchanged = (flags & 0x8000) != 0,
            IntentToAdd = (flags & 0x4000) != 0,
            SkipWorktree = (flags & 0x2000) != 0,
            Stage = (flags >> 12) & 3
        }, total);
    }
}

public record class ConflictInfo
{
    public string Path { get; init; } = string.Empty;
    public ConflictResolution Resolution { get; set; }
    public Hash? OursHash { get; init; }
    public Hash? TheirsHash { get; init; }
    public Hash? BaseHash { get; init; }
    public byte[]? OursContent { get; set; }
    public byte[]? TheirsContent { get; set; }
    public byte[]? BaseContent { get; set; }

    /// <summary>Number of conflicting regions in this file.</summary>
    public int HunkCount { get; set; }

    /// <summary>
    /// The three versions as text, so a caller can resolve a conflict programmatically
    /// instead of parsing conflict markers out of the working tree.
    /// </summary>
    public string OursText => OursContent != null ? System.Text.Encoding.UTF8.GetString(OursContent) : "";
    public string TheirsText => TheirsContent != null ? System.Text.Encoding.UTF8.GetString(TheirsContent) : "";
    public string BaseText => BaseContent != null ? System.Text.Encoding.UTF8.GetString(BaseContent) : "";
}