namespace SourceManager;

public static class GcCmd
{
    /// <summary>
    /// Objects younger than this are never pruned. A blob that was just written by `sm add`
    /// is only referenced by the index, and an in-flight operation in another process may not
    /// have written its ref yet — a grace period is what makes pruning safe rather than lucky.
    /// </summary>
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromDays(14);

    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool quiet = args.GetBoolOption("quiet");
        bool noPrune = args.GetBoolOption("no-prune");
        bool aggressive = args.GetBoolOption("aggressive");

        // --prune takes an OPTIONAL date argument. It must be registered as a value option
        // (otherwise `--prune` alone is meaningless) and the presence of the option — not the
        // parseability of its value — is what enables pruning.
        string? pruneSpec = args.GetOption("prune");
        bool pruneRequested = args.WasProvided("prune");
        TimeSpan grace = DefaultGracePeriod;
        if (pruneRequested && !string.IsNullOrWhiteSpace(pruneSpec))
        {
            if (TryParseGracePeriod(pruneSpec!, out TimeSpan parsed))
                grace = parsed;
        }
        if (pruneRequested) noPrune = false;

        if (!quiet)
            Terminal.WriteLine("Running garbage collection...", Terminal.Color.BrightCyan);

        string objectsDir = Path.Combine(repo.SmPath, "objects");
        if (!Directory.Exists(objectsDir))
        {
            if (!quiet) Terminal.WriteSuccess("Nothing to collect.");
            return 0;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        CollectAllReferencedObjects(repo, reachable);

        int beforeCount = CountObjects(repo);
        int removed = 0;
        var keptByGrace = new List<string>();

        if (!noPrune)
        {
            DateTime cutoff = DateTime.UtcNow - grace;

            foreach (string objectFile in Directory.GetFiles(objectsDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(objectsDir, objectFile).Replace('\\', '/');
                string hashHex = relativePath.Replace("/", "");
                if (hashHex.Length < 64) continue;
                hashHex = hashHex[..64];

                if (reachable.Contains(hashHex)) continue;

                // Unreachable, but too new: it may be an in-flight write from another process
                // or a staged blob that the index has not been flushed for yet.
                if (File.GetLastWriteTimeUtc(objectFile) > cutoff)
                {
                    keptByGrace.Add(hashHex);
                    continue;
                }

                try
                {
                    File.Delete(objectFile);
                    removed++;
                }
                catch { }
            }

            CleanEmptyDirectories(objectsDir);
        }

        int afterCount = CountObjects(repo);

        if (!quiet)
        {
            Console.WriteLine($"Objects: {beforeCount} before, {afterCount} after.");
            if (noPrune)
                Console.WriteLine("Pruning disabled (--no-prune); nothing was removed.");
            else
                Console.WriteLine($"Removed {removed} unreachable object(s).");
            if (keptByGrace.Count > 0)
                Console.WriteLine($"Kept {keptByGrace.Count} unreachable object(s) inside the {FormatSpan(grace)} grace period.");
            if (aggressive)
                Console.WriteLine("note: --aggressive has no effect; sm does not use pack files.");
            Terminal.WriteSuccess("Garbage collection complete.");
        }

        return 0;
    }

    private static string FormatSpan(TimeSpan span)
        => span.TotalDays >= 1 ? $"{span.TotalDays:0.##} day" : $"{span.TotalHours:0.##} hour";

    /// <summary>Accepts "now", "2.weeks.ago", "3.days.ago", "1.month.ago" or an absolute date.</summary>
    private static bool TryParseGracePeriod(string spec, out TimeSpan grace)
    {
        if (DateSpec.TryParseSince(spec, out _, out TimeSpan delta))
        {
            grace = delta;
            return true;
        }
        grace = DefaultGracePeriod;
        return false;
    }

    /// <summary>
    /// Computes every object that must survive collection. The index is a root: staged content
    /// that has never been committed is still referenced, and treating it as garbage destroyed
    /// the user's staged work.
    /// </summary>
    private static void CollectAllReferencedObjects(Repository repo, HashSet<string> reachable)
    {
        var commits = new List<Hash>();
        var directRoots = new List<Hash>();

        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            Hash? hash = repo.Refs.ResolveRefName(refName);
            if (hash.HasValue)
            {
                if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
                {
                    // A tag ref may point at a tag object or directly at a commit.
                    if (repo.Objects.ReadObjectWithType(hash.Value)?.type == ObjectType.Tag)
                    {
                        directRoots.Add(hash.Value);
                        TagObject? tagObj = repo.Objects.ReadTag(hash.Value);
                        if (tagObj != null) commits.Add(tagObj.TargetHash);
                    }
                    else
                    {
                        commits.Add(hash.Value);
                    }
                }
                else
                {
                    commits.Add(hash.Value);
                }
            }
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (headCommit.HasValue) commits.Add(headCommit.Value);

        foreach (Hash? h in new[]
                 {
                     repo.Refs.GetMergeHead(), repo.Refs.GetCherryPickHead(),
                     repo.Refs.GetRevertHead()
                 })
        {
            if (h.HasValue) commits.Add(h.Value);
        }

        foreach (var stash in repo.Refs.GetStashes())
            commits.Add(stash.CommitHash);

        // Index roots: staged blobs are live regardless of whether a commit exists yet.
        foreach (var (_, entry) in repo.Index.GetAllEntries())
        {
            reachable.Add(entry.ObjectHash.ToHex());
        }

        foreach (Hash root in directRoots)
            reachable.Add(root.ToHex());

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Hash>();
        foreach (Hash c in commits)
        {
            if (reachable.Add(c.ToHex()))
                queue.Enqueue(c);
        }

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!processed.Add(hex)) continue;
            reachable.Add(hex);

            Commit? commit = repo.Objects.ReadCommit(current);
            if (commit != null)
            {
                if (commit.TreeHash.HasValue)
                    CollectTreeObjects(repo, commit.TreeHash.Value, reachable);
                foreach (var p in commit.ParentHashes)
                {
                    if (reachable.Add(p.ToHex()))
                        queue.Enqueue(p);
                }
                continue;
            }

            // Only decode a tag when the payload really is one; TagObject.Deserialize rejects
            // non-tag payloads, but the type check avoids paying for the parse at all.
            if (repo.Objects.ReadObjectWithType(current)?.type == ObjectType.Tag)
            {
                TagObject? tag = repo.Objects.ReadTag(current);
                if (tag != null && reachable.Add(tag.TargetHash.ToHex()))
                    queue.Enqueue(tag.TargetHash);
            }
        }
    }

    private static void CollectTreeObjects(Repository repo, Hash treeHash, HashSet<string> reachable)
    {
        var queue = new Queue<Hash>();
        queue.Enqueue(treeHash);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!reachable.Add(current.ToHex())) continue;

            Tree? tree = repo.Objects.ReadTree(current);
            if (tree == null) continue;

            foreach (var entry in tree.Entries)
            {
                reachable.Add(entry.ObjectHash.ToHex());
                if (entry.Mode == FileMode.Directory)
                    queue.Enqueue(entry.ObjectHash);
            }
        }
    }

    private static int CountObjects(Repository repo)
    {
        string objectsDir = Path.Combine(repo.SmPath, "objects");
        if (!Directory.Exists(objectsDir)) return 0;

        int count = 0;
        foreach (string dir in Directory.GetDirectories(objectsDir))
        {
            if (Path.GetFileName(dir) == "pack") continue;
            foreach (string file in Directory.GetFiles(dir))
            {
                count++;
            }
        }
        return count;
    }

    private static void CleanEmptyDirectories(string rootDir)
    {
        foreach (string dir in Directory.GetDirectories(rootDir))
        {
            CleanEmptyDirectories(dir);
            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                try { Directory.Delete(dir); } catch { }
            }
        }
    }

    private static void PruneOldObjects(Repository repo, string dateSpec, HashSet<string> reachable)
    {
        if (!DateTime.TryParse(dateSpec, out DateTime cutoff))
        {
            cutoff = dateSpec.ToLowerInvariant() switch
            {
                "now" => DateTime.Now,
                "yesterday" => DateTime.Now.AddDays(-1),
                "2.weeks.ago" => DateTime.Now.AddDays(-14),
                "1.month.ago" => DateTime.Now.AddMonths(-1),
                _ => DateTime.Now.AddDays(-14)
            };
        }

        string objectsDir = Path.Combine(repo.SmPath, "objects");
        foreach (string dir in Directory.GetDirectories(objectsDir))
        {
            if (Path.GetFileName(dir) == "pack") continue;
            foreach (string file in Directory.GetFiles(dir))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoff)
                {
                    string relativePath = Path.GetRelativePath(objectsDir, file).Replace('\\', '/');
                    string hex = relativePath.Replace("/", "");
                    if (hex.Length >= 64 && !reachable.Contains(hex[..64]))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
    }
}