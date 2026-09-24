using System.Text;

namespace SourceManager;

/// <summary>
/// Repository integrity verification.
///
/// Content addressing is only a guarantee if it is actually checked, so this walks every object,
/// recomputes its identifier from its stored payload, and verifies that every reference inside
/// every object resolves. It reports each problem as a structured finding rather than throwing,
/// so a partially damaged repository can still be diagnosed.
/// </summary>
public static class VerifyEngine
{
    public sealed record Finding(string Severity, string Code, string Message, string? Path = null);

    public sealed class Report
    {
        public int ObjectCount { get; set; }
        public int CommitCount { get; set; }
        public int TreeCount { get; set; }
        public int BlobCount { get; set; }
        public int TagCount { get; set; }
        public int BytesOnDisk { get; set; }
        public List<Finding> Findings { get; } = new();

        public bool Ok => Findings.All(f => f.Severity != "error");

        public void Error(string code, string message, string? path = null)
            => Findings.Add(new Finding("error", code, message, path));

        public void Warn(string code, string message, string? path = null)
            => Findings.Add(new Finding("warning", code, message, path));

        public int ErrorCount => Findings.Count(f => f.Severity == "error");
        public int WarningCount => Findings.Count(f => f.Severity == "warning");
    }

    public static Report Run(Repository repo, bool checkUnreachable = false)
    {
        var report = new Report();
        var objectsDir = Path.Combine(repo.SmPath, "objects");
        if (!Directory.Exists(objectsDir))
        {
            report.Error("no-object-database", "object database directory is missing");
            return report;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);

        // ── Pass 1: every stored object must hash to its own file name ───────────────
        foreach (string file in Directory.GetFiles(objectsDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(objectsDir, file).Replace('\\', '/');
            string hex = relative.Replace("/", "");
            if (hex.Length != 64 || !IsHex(hex))
            {
                report.Warn("stray-file", $"file in the object database is not a loose object: {relative}");
                continue;
            }

            present.Add(hex);
            report.ObjectCount++;
            report.BytesOnDisk += (int)new FileInfo(file).Length;

            var typed = repo.Objects.ReadObjectWithType(Hash.Parse(hex));
            if (typed == null)
            {
                report.Error("unreadable", $"object cannot be decompressed or parsed: {hex[..12]}", relative);
                continue;
            }

            // Recompute the id from the payload. A mismatch means the bytes were altered or
            // the object was stored under the wrong name.
            Hash actual = typed.Value.type switch
            {
                ObjectType.Blob => HashUtil.ComputeBlob(typed.Value.data),
                ObjectType.Tree => HashUtil.ComputeTree(typed.Value.data),
                ObjectType.Commit => HashUtil.ComputeCommit(typed.Value.data),
                ObjectType.Tag => HashUtil.ComputeTag(typed.Value.data),
                _ => Hash.Zero
            };

            if (!actual.ToHex().Equals(hex, StringComparison.Ordinal))
            {
                report.Error("hash-mismatch",
                    $"object {hex[..12]} does not match its content (recomputed {actual.Short})", relative);
                continue;
            }

            switch (typed.Value.type)
            {
                case ObjectType.Commit: report.CommitCount++; break;
                case ObjectType.Tree: report.TreeCount++; break;
                case ObjectType.Blob: report.BlobCount++; break;
                case ObjectType.Tag: report.TagCount++; break;
            }
        }

        // ── Pass 2: every reference inside every object must resolve ─────────────────
        foreach (string hex in present)
        {
            var typed = repo.Objects.ReadObjectWithType(Hash.Parse(hex));
            if (typed == null) continue;

            switch (typed.Value.type)
            {
                case ObjectType.Commit:
                {
                    Commit? commit = ObjectStore.DeserializeCommit(typed.Value.data);
                    if (commit == null) continue;

                    if (commit.TreeHash is { } tree && !present.Contains(tree.ToHex()))
                        report.Error("missing-tree", $"commit {hex[..12]} references missing tree {tree.Short}", hex);

                    foreach (Hash parent in commit.ParentHashes)
                    {
                        if (!present.Contains(parent.ToHex()))
                            report.Error("missing-parent", $"commit {hex[..12]} references missing parent {parent.Short}", hex);
                    }
                    break;
                }
                case ObjectType.Tree:
                {
                    Tree? tree = ObjectStore.DeserializeTree(typed.Value.data);
                    if (tree == null)
                    {
                        report.Error("malformed-tree", $"tree {hex[..12]} cannot be parsed", hex);
                        continue;
                    }
                    foreach (var entry in tree.Entries)
                    {
                        if (!present.Contains(entry.ObjectHash.ToHex()))
                            report.Error("missing-entry",
                                $"tree {hex[..12]} references missing object {entry.ObjectHash.Short} ({entry.Name})", hex);
                    }
                    break;
                }
                case ObjectType.Tag:
                {
                    TagObject? tag = ObjectStore.DeserializeTag(typed.Value.data);
                    if (tag == null)
                    {
                        report.Error("malformed-tag", $"tag {hex[..12]} cannot be parsed", hex);
                        continue;
                    }
                    if (!present.Contains(tag.TargetHash.ToHex()))
                        report.Error("missing-target", $"tag {hex[..12]} references missing object {tag.TargetHash.Short}", hex);
                    break;
                }
            }
        }

        // ── Pass 3: refs and the index must point at real objects ────────────────────
        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            Hash? target = repo.Refs.ResolveRefName(refName);
            if (target == null)
            {
                report.Error("broken-ref", $"ref '{refName}' has no resolvable value");
                continue;
            }
            if (!present.Contains(target.Value.ToHex()))
                report.Error("dangling-ref", $"ref '{refName}' points at missing object {target.Value.Short}");
        }

        foreach (var (path, entry) in repo.Index.GetAllEntries())
        {
            if (!present.Contains(entry.ObjectHash.ToHex()))
                report.Error("missing-index-object",
                    $"index entry '{path}' references missing object {entry.ObjectHash.Short}");
        }

        // ── Pass 4: reachability from refs, HEAD, index and stashes ─────────────────
        var roots = new List<Hash>();
        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            Hash? t = repo.Refs.ResolveRefName(refName);
            if (t.HasValue) roots.Add(t.Value);
        }
        if (repo.Refs.GetHeadCommit() is { } head && !head.Equals(Hash.Zero)) roots.Add(head);
        foreach (var stash in repo.Refs.GetStashes()) roots.Add(stash.CommitHash);
        foreach (var (_, entry) in repo.Index.GetAllEntries()) roots.Add(entry.ObjectHash);

        var queue = new Queue<Hash>(roots);
        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!reachable.Add(current.ToHex())) continue;
            var typed = repo.Objects.ReadObjectWithType(current);
            if (typed == null) continue;

            switch (typed.Value.type)
            {
                case ObjectType.Commit:
                    Commit? commit = ObjectStore.DeserializeCommit(typed.Value.data);
                    if (commit?.TreeHash is { } tree) queue.Enqueue(tree);
                    if (commit != null)
                        foreach (Hash p in commit.ParentHashes) queue.Enqueue(p);
                    break;
                case ObjectType.Tree:
                    Tree? t = ObjectStore.DeserializeTree(typed.Value.data);
                    if (t != null)
                        foreach (var e in t.Entries) queue.Enqueue(e.ObjectHash);
                    break;
                case ObjectType.Tag:
                    TagObject? tag = ObjectStore.DeserializeTag(typed.Value.data);
                    if (tag != null) queue.Enqueue(tag.TargetHash);
                    break;
            }
        }

        if (checkUnreachable)
        {
            foreach (string hex in present)
            {
                if (!reachable.Contains(hex))
                    report.Warn("unreachable", $"object {hex[..12]} is not reachable from any ref or the index");
            }
        }

        return report;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }
}
