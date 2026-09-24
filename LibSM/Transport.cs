using System.Text;
using System.Text.Json;

namespace SourceManager;

/// <summary>
/// Abstract transport layer that provides ALL remote repository operations.
/// Supports the native sm:// protocol, plus file://, http://, https:// and ssh:// as
/// compatibility carriers.
/// </summary>
public abstract class Transport : IDisposable
{
    // ─── Refs ───────────────────────────────────────────────────────
    public abstract Hash? GetHeadCommit();
    public abstract string? GetCurrentBranch();
    public abstract List<BranchInfo> ListBranches();
    public abstract Hash? GetBranch(string branchName);
    public abstract void SetBranch(string branchName, Hash hash, string? message = null);
    public abstract void CreateBranch(string branchName, Hash hash);
    public abstract void DeleteBranch(string branchName);
    public abstract List<string> ListTags();
    public abstract Hash? GetTag(string tagName);
    public abstract void SetTag(string tagName, Hash hash, string? message = null);
    public abstract void DeleteTag(string tagName);
    public abstract Hash? GetRemoteBranch(string remoteName, string branchName);
    public abstract void SetRemoteBranch(string remoteName, string branchName, Hash hash);
    public abstract void DeleteRemoteBranch(string remoteName, string branchName);
    public abstract List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null);

    // ─── Objects ────────────────────────────────────────────────────
    public abstract byte[]? ReadObjectRaw(Hash hash);
    public abstract (ObjectType type, byte[] compressed)? ReadObjectWithType(Hash hash);
    public abstract bool Exists(Hash hash);
    /// <summary>
    /// Stores an object payload that has ALREADY been compressed, exactly as received.
    /// Prefer <see cref="WriteObjectsBatch"/> for protocol payloads, which are uncompressed.
    /// </summary>
    public abstract void WriteObjectRaw(Hash hash, byte[] compressedData, ObjectType type);

    /// <summary>
    /// Stores a batch of raw object payloads, framing them with their declared type and
    /// compressing them for storage. Protocol payloads are uncompressed, and writing them
    /// verbatim produced object files that no reader could decompress.
    /// </summary>
    public abstract void WriteObjectsBatch(Dictionary<Hash, (ObjectType type, byte[] data)> objects);

    // Convenience methods (can be overridden for efficiency)
    public virtual Commit? ReadCommit(Hash hash)
    {
        byte[]? raw = ReadObjectRaw(hash);
        if (raw == null) return null;
        try { return ObjectStore.DeserializeCommit(raw); }
        catch { return null; }
    }

    public virtual Tree? ReadTree(Hash hash)
    {
        byte[]? raw = ReadObjectRaw(hash);
        if (raw == null) return null;
        try { return ObjectStore.DeserializeTree(raw); }
        catch { return null; }
    }

    public virtual byte[]? ReadBlob(Hash hash)
    {
        byte[]? raw = ReadObjectRaw(hash);
        if (raw == null) return null;
        return ObjectStore.DeserializeBlob(raw);
    }

    public virtual TagObject? ReadTag(Hash hash)
    {
        byte[]? raw = ReadObjectRaw(hash);
        if (raw == null) return null;
        try { return ObjectStore.DeserializeTag(raw); }
        catch { return null; }
    }

    // ─── Operations ─────────────────────────────────────────────────
    public abstract bool IsRepository();
    public abstract Hash? FindMergeBase(Hash a, Hash b);
    public abstract bool IsAncestor(Hash ancestor, Hash descendant);

    // Walking objects for transport
    public abstract Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjects(List<Hash> wants, List<Hash> haves);

    // ─── Factory ────────────────────────────────────────────────────
    public static Transport Create(string url, string? remoteName = null)
    {
        if (url.StartsWith("file://"))
            return new FileTransport(url, remoteName);
        else if (url.StartsWith("sm://"))
            return new SmTcpTransport(url, remoteName);
        else if (url.StartsWith("http://") || url.StartsWith("https://"))
            return new HttpTransport(url, remoteName);
        else if (url.StartsWith("ssh://"))
            return new SshTransport(url, remoteName);
        else
            throw new NotSupportedException($"Unsupported transport protocol: {url.Split(':')[0]}");
    }

    public virtual void Dispose() { }
}

// ─── FileTransport ─────────────────────────────────────────────────
public class FileTransport : Transport
{
    private readonly Repository _repo;
    private readonly string _path;

    public FileTransport(string url, string? remoteName)
    {
        // "file://" + path. On Windows a URL such as file:///C:/repos/x yields "/C:/repos/x",
        // and that leading slash made the path unopenable — remote access on Windows was
        // effectively broken.
        string raw = url[7..];
        if (Helpers.IsWindows && raw.Length >= 3 && raw[0] == '/' && char.IsLetter(raw[1]) && raw[2] == ':')
            raw = raw[1..];
        else if (!Helpers.IsWindows)
            raw = "/" + raw;

        _path = Path.GetFullPath(raw);

        if (!Repository.IsAnyRepository(_path))
            throw new InvalidOperationException($"Not a repository: {_path}");
        _repo = Repository.Open(_path);
    }

    public override Hash? GetHeadCommit() => _repo.Refs.GetHeadCommit();
    public override string? GetCurrentBranch() => _repo.Refs.GetCurrentBranch();

    public override List<BranchInfo> ListBranches() => _repo.Refs.ListBranches();

    public override Hash? GetBranch(string branchName) => _repo.Refs.GetBranch(branchName);

    public override void SetBranch(string branchName, Hash hash, string? message = null)
        => _repo.Refs.SetBranch(branchName, hash, message);

    public override void CreateBranch(string branchName, Hash hash)
        => _repo.Refs.CreateBranch(branchName, hash);

    public override void DeleteBranch(string branchName) => _repo.Refs.DeleteBranch(branchName);

    public override List<string> ListTags() => _repo.Refs.ListTags();

    public override Hash? GetTag(string tagName) => _repo.Refs.GetTag(tagName);

    public override void SetTag(string tagName, Hash hash, string? message = null)
        => _repo.Refs.SetTag(tagName, hash, message);

    public override void DeleteTag(string tagName) => _repo.Refs.DeleteTag(tagName);

    public override Hash? GetRemoteBranch(string remoteName, string branchName)
        => _repo.Refs.GetRemoteBranch(remoteName, branchName);

    public override void SetRemoteBranch(string remoteName, string branchName, Hash hash)
        => _repo.Refs.SetRemoteBranch(remoteName, branchName, hash);

    public override void DeleteRemoteBranch(string remoteName, string branchName)
        => _repo.Refs.DeleteRemoteBranch(remoteName, branchName);

    public override List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null)
        => _repo.Refs.ListRemoteBranches(remoteName);

    public override byte[]? ReadObjectRaw(Hash hash)
        => _repo.Objects.ReadObjectRaw(hash);

    public override (ObjectType type, byte[] compressed)? ReadObjectWithType(Hash hash)
    {
        var result = _repo.Objects.ReadObjectWithType(hash);
        if (result.HasValue)
            return (result.Value.type, result.Value.data);
        return null;
    }

    public override bool Exists(Hash hash) => _repo.Objects.Exists(hash);

    public override void WriteObjectRaw(Hash hash, byte[] compressedData, ObjectType type)
    {
        string path = _repo.Objects.GetObjectPath(hash);
        string? dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, compressedData);
    }

    public override void WriteObjectsBatch(Dictionary<Hash, (ObjectType type, byte[] data)> objects)
    {
        foreach (var (hash, (type, data)) in objects)
        {
            string path = _repo.Objects.GetObjectPath(hash);
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            if (File.Exists(path)) continue;
            File.WriteAllBytes(path, ObjectStore.CompressFramed(type, data));
        }
    }

    public override bool IsRepository() => true;

    public override Hash? FindMergeBase(Hash a, Hash b) => _repo.FindMergeBase(a, b);

    /// <summary>
    /// True when <paramref name="ancestor"/> is reachable from <paramref name="descendant"/>
    /// by walking parents — i.e. the descendant's history contains the ancestor.
    /// </summary>
    public override bool IsAncestor(Hash ancestor, Hash descendant)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<Hash>();
        queue.Enqueue(descendant);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (current.Equals(ancestor)) return true;

            Commit? commit = _repo.Objects.ReadCommit(current);
            if (commit != null)
            {
                foreach (Hash p in commit.ParentHashes)
                {
                    if (!visited.Contains(p.ToHex()))
                        queue.Enqueue(p);
                }
            }
        }
        return false;
    }

    public override Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjects(List<Hash> wants, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();
        var haveSet = new HashSet<string>(haves.Select(h => h.ToHex()));
        var visited = new HashSet<string>();
        var queue = new Queue<Hash>(wants);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = _repo.Objects.ReadObjectWithType(current);
            if (obj.HasValue)
            {
                result[current] = (obj.Value.type, obj.Value.data);

                if (obj.Value.type == ObjectType.Commit)
                {
                    Commit? commit = ObjectStore.DeserializeCommit(obj.Value.data);
                    if (commit != null)
                    {
                        foreach (Hash p in commit.ParentHashes)
                            queue.Enqueue(p);
                        if (commit.TreeHash.HasValue)
                            CollectTreeObjects(commit.TreeHash.Value, result, visited, haveSet);
                    }
                }
                else if (obj.Value.type == ObjectType.Tag)
                {
                    TagObject? tag = ObjectStore.DeserializeTag(obj.Value.data);
                    if (tag != null)
                        queue.Enqueue(tag.TargetHash);
                }
            }
        }

        return result;
    }

    private void CollectTreeObjects(Hash treeHash, Dictionary<Hash, (ObjectType type, byte[] data)> result,
        HashSet<string> visited, HashSet<string> haveSet)
    {
        var queue = new Queue<Hash>();
        queue.Enqueue(treeHash);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = _repo.Objects.ReadObjectWithType(current);
            if (!obj.HasValue) continue;

            result[current] = (obj.Value.type, obj.Value.data);

            if (obj.Value.type == ObjectType.Tree)
            {
                Tree? tree = ObjectStore.DeserializeTree(obj.Value.data);
                if (tree != null)
                {
                    foreach (var entry in tree.Entries)
                    {
                        // Always include the blob/tree object
                        string entryHex = entry.ObjectHash.ToHex();
                        if (!visited.Contains(entryHex) && !haveSet.Contains(entryHex))
                        {
                            result[entry.ObjectHash] = (ObjectType.Blob, Array.Empty<byte>()); // placeholder, filled below
                        }

                        if (entry.Mode == FileMode.Directory)
                            CollectTreeObjects(entry.ObjectHash, result, visited, haveSet);
                        else
                        {
                            var blobObj = _repo.Objects.ReadObjectWithType(entry.ObjectHash);
                            if (blobObj.HasValue && !result.ContainsKey(entry.ObjectHash))
                                result[entry.ObjectHash] = (blobObj.Value.type, blobObj.Value.data);
                        }
                    }
                }
            }
        }
    }

    public override void Dispose() { }
}

// ─── JSON helpers for Transport ────────────────────────────────────
internal static class TransportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(object obj) => JsonSerializer.Serialize(obj, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    public static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}

internal static class TransportProtocol
{
    public const string ProtocolVersion = "1.0";

    // HTTP endpoint paths
    public const string HeadEndpoint = "/sm/head";
    public const string BranchesEndpoint = "/sm/refs/heads";
    public const string TagsEndpoint = "/sm/refs/tags";
    public const string RemotesEndpoint = "/sm/refs/remotes";
    public const string ObjectsEndpoint = "/sm/objects";
    public const string ObjectsBatchEndpoint = "/sm/objects/batch";
    public const string ObjectsCheckEndpoint = "/sm/objects/check";
    public const string MergeBaseEndpoint = "/sm/merge-base";
    public const string AncestorEndpoint = "/sm/ancestor";
    public const string CollectObjectsEndpoint = "/sm/collect-objects";
    public const string IsRepoEndpoint = "/sm/is-repo";
    public const string LockEndpoint = "/sm/lock";
    public const string HealthEndpoint = "/sm/health";
}