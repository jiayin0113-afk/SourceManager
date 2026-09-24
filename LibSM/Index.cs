using System.Text;

namespace SourceManager;

public class Index
{
    private readonly string _indexPath;
    private List<IndexEntry> _entries = new();
    private bool _dirty;

    public Index(string repoPath)
    {
        _indexPath = Path.Combine(repoPath, ".sm", "index");
        Load();
    }

    public IReadOnlyList<IndexEntry> Entries => _entries;

    public void Load()
    {
        _entries.Clear();
        if (!File.Exists(_indexPath)) return;

        byte[] data = File.ReadAllBytes(_indexPath);
        const int ChecksumSize = 32;
        if (data.Length < 12 + ChecksumSize)
            throw new InvalidDataException($"Index file is truncated or corrupt: {_indexPath}");

        // The trailing 32 bytes are the SHA-256 of everything before them. Verifying on load
        // catches a truncated, hand-edited or clobbered index instead of trusting garbage
        // entry counts and paths.
        int contentLength = data.Length - ChecksumSize;
        byte[] expected = System.Security.Cryptography.SHA256.HashData(data.AsSpan(0, contentLength).ToArray());
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                expected, data.AsSpan(contentLength)))
        {
            throw new InvalidDataException(
                $"Index checksum mismatch: {_indexPath} is corrupt or was modified outside SourceManager.");
        }

        int entryCount = BitConverter.ToInt32(data.AsSpan(8, 4));
        int offset = 12;
        for (int i = 0; i < entryCount && offset < contentLength; i++)
        {
            var (entry, consumed) = IndexEntry.Deserialize(data.AsSpan(offset, contentLength - offset));
            _entries.Add(entry);
            offset += consumed;
        }
        _dirty = false;
    }

    public void Save()
    {
        if (!_dirty) return;

        PathUtil.EnsureDirectory(Path.GetDirectoryName(_indexPath)!);
        var ms = new MemoryStream();
        byte[] header = new byte[12];
        header[0] = (byte)'D';
        header[1] = (byte)'I';
        header[2] = (byte)'R';
        header[3] = (byte)'C';
        BitConverter.TryWriteBytes(header.AsSpan(4), 2);
        BitConverter.TryWriteBytes(header.AsSpan(8), _entries.Count);
        ms.Write(header, 0, header.Length);

        foreach (var entry in _entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            byte[] serialized = entry.Serialize();
            ms.Write(serialized, 0, serialized.Length);
        }

        byte[] checksum = System.Security.Cryptography.SHA256.HashData(ms.ToArray());
        ms.Write(checksum, 0, checksum.Length);

        string tmp = _indexPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, _indexPath, true);
        _dirty = false;
    }

    public void Add(string path, Hash objectHash, FileMode mode)
    {
        var fileInfo = new FileInfo(Path.Combine(Path.GetDirectoryName(_indexPath)!, "..", path));
        var entry = new IndexEntry
        {
            CTime = fileInfo.CreationTimeUtc,
            MTime = fileInfo.LastWriteTimeUtc,
            Device = 0,
            Inode = 0,
            Mode = mode,
            Uid = 0,
            Gid = 0,
            FileSize = fileInfo.Length,
            ObjectHash = objectHash,
            Path = PathUtil.NormalizePath(path),
            Flags = (ushort)Math.Min(path.Length, 0xFFF),
            Stage = 0
        };

        int existing = _entries.FindIndex(e => e.Path == entry.Path);
        if (existing >= 0)
            _entries[existing] = entry;
        else
            _entries.Add(entry);
        _dirty = true;
    }

    public void Remove(string path)
    {
        _entries.RemoveAll(e => e.Path == PathUtil.NormalizePath(path));
        _dirty = true;
    }

    public IndexEntry? GetEntry(string path)
    {
        return _entries.Find(e => e.Path == PathUtil.NormalizePath(path));
    }

    public bool Contains(string path)
    {
        return _entries.Any(e => e.Path == PathUtil.NormalizePath(path));
    }

    public void Clear()
    {
        _entries.Clear();
        _dirty = true;
    }

    public Dictionary<string, Hash> GetTrackedFiles()
    {
        return _entries.ToDictionary(e => e.Path, e => e.ObjectHash);
    }

    public bool HasChanges(WorkingTreeStatus status)
    {
        return _dirty || status.Changes.Count > 0 || status.Untracked.Count > 0;
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public IEnumerable<(string path, IndexEntry entry)> GetAllEntries()
    {
        return _entries.Select(e => (e.Path, e));
    }
}