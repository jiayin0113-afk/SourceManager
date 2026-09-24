namespace SourceManager;

public static class FsckCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool unreachable = args.GetBoolOption("unreachable");
        bool dangling = args.GetBoolOption("dangling");
        bool tags = args.GetBoolOption("tags");
        bool root = args.GetBoolOption("root");
        bool cache = args.GetBoolOption("cache");
        bool noReflogs = args.GetBoolOption("no-reflogs");
        bool full = args.GetBoolOption("full");
        bool strict = args.GetBoolOption("strict");
        bool verbose = args.GetBoolOption("verbose");
        bool lostFound = args.GetBoolOption("lost-found");
        bool nameObjects = args.GetBoolOption("name-objects");
        bool noDangling = args.GetBoolOption("no-dangling");

        Terminal.WriteLine("Checking object database...", Terminal.Color.BrightCyan);
        int errors = 0;
        int warnings = 0;

        string objectsDir = Path.Combine(repo.SmPath, "objects");
        if (!Directory.Exists(objectsDir))
        {
            Terminal.WriteError("error: no object database found");
            return 1;
        }

        var reachableObjects = new HashSet<string>();
        var allObjectFiles = new HashSet<string>();

        foreach (string dir in Directory.GetDirectories(objectsDir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName == "pack") continue;
            foreach (string file in Directory.GetFiles(dir))
            {
                string hex = dirName + Path.GetFileName(file);
                allObjectFiles.Add(hex);
            }
        }

        var corruptObjects = new List<string>();
        foreach (string hex in allObjectFiles)
        {
            Hash hash = Hash.Parse(hex[..64]);

            bool success = repo.Objects.Exists(hash);
            if (success && strict)
            {
                try
                {
                    var parsed = repo.Objects.ReadObject(hash);
                    if (parsed == null && full)
                    {
                        corruptObjects.Add(hex[..64]);
                        errors++;
                        if (verbose) Terminal.WriteError($"error: corrupt object {hex[..64]}");
                    }
                }
                catch
                {
                    corruptObjects.Add(hex[..64]);
                    errors++;
                    if (verbose) Terminal.WriteError($"error: corrupt object {hex[..64]}");
                }
            }
            else if (!success && full)
            {
                corruptObjects.Add(hex[..64]);
            }
        }

        CollectAllReferencedObjects(repo, reachableObjects);

        if (!noDangling)
        {
            var danglingObjects = new List<string>();
            foreach (string hex in allObjectFiles)
            {
                if (!reachableObjects.Contains(hex[..64]))
                    danglingObjects.Add(hex[..64]);
            }

            if (dangling || !noDangling)
            {
                int danglingCount = 0;
                foreach (string hex in danglingObjects)
                {
                    if (danglingCount >= (verbose ? 100 : 10))
                    {
                        Console.WriteLine($"... and {danglingObjects.Count - 10} more dangling objects");
                        break;
                    }

                    Hash hash = Hash.Parse(hex);
                    if (repo.Objects.Exists(hash))
                    {
                        var objWithType = repo.Objects.ReadObjectWithType(hash);
                        if (objWithType != null)
                        {
                            var (type, _) = objWithType.Value;
                            if (type == ObjectType.Commit)
                                Console.WriteLine($"dangling commit {hex[..64]}");
                            else if (type == ObjectType.Tree)
                                Console.WriteLine($"dangling tree {hex[..64]}");
                            else if (type == ObjectType.Blob)
                                Console.WriteLine($"dangling blob {hex[..64]}");
                            else if (type == ObjectType.Tag)
                                Console.WriteLine($"dangling tag {hex[..64]}");
                        }
                    }
                    danglingCount++;
                }
            }
        }

        if (unreachable || verbose)
        {
            var unreachableObjects = new List<string>();
            foreach (string hex in allObjectFiles)
            {
                if (!reachableObjects.Contains(hex[..64]) && !corruptObjects.Contains(hex[..64]))
                    unreachableObjects.Add(hex[..64]);
            }

            if (unreachableObjects.Count > 0)
            {
                Console.WriteLine($"unreachable: {unreachableObjects.Count} objects");
                if (verbose)
                {
                    foreach (string hex in unreachableObjects.Take(20))
                        Console.WriteLine($"  {hex}");
                }
            }
        }

        CheckRefs(repo, ref errors, ref warnings, verbose);
        CheckHead(repo, ref errors, ref warnings, verbose);
        CheckIndex(repo, ref errors, ref warnings, verbose);

        if (lostFound)
        {
            string lostFoundDir = Path.Combine(repo.SmPath, "lost-found");
            string lostFoundCommitDir = Path.Combine(lostFoundDir, "commits");
            string lostFoundOtherDir = Path.Combine(lostFoundDir, "other");

            var unreachableFiltered = allObjectFiles
                .Where(h => !reachableObjects.Contains(h[..64]))
                .ToList();

            foreach (string hex in unreachableFiltered)
            {
                Hash hash = Hash.Parse(hex[..64]);
                var objWithType = repo.Objects.ReadObjectWithType(hash);
                if (objWithType != null)
                {
                    var (type, _) = objWithType.Value;
                    if (type == ObjectType.Commit)
                    {
                        Directory.CreateDirectory(lostFoundCommitDir);
                        string destPath = Path.Combine(lostFoundCommitDir, hex[..64]);
                        string srcPath = Path.Combine(objectsDir, hex[..2], hex[2..]);
                        if (File.Exists(srcPath) && !File.Exists(destPath))
                            File.Copy(srcPath, destPath);
                    }
                    else if (type == ObjectType.Tree || type == ObjectType.Blob)
                    {
                        Directory.CreateDirectory(lostFoundOtherDir);
                        string destPath = Path.Combine(lostFoundOtherDir, hex[..64]);
                        string srcPath = Path.Combine(objectsDir, hex[..2], hex[2..]);
                        if (File.Exists(srcPath) && !File.Exists(destPath))
                            File.Copy(srcPath, destPath);
                    }
                }
            }
            Console.WriteLine($"Moved unreachable objects to {lostFoundDir}");
        }

        if (errors == 0 && warnings == 0)
        {
            Terminal.WriteSuccess("Fsck completed: no errors found.");
        }
        else
        {
            Terminal.WriteLine($"Fsck completed: {errors} error(s), {warnings} warning(s).",
                errors > 0 ? Terminal.Color.Red : Terminal.Color.Yellow);
        }

        return errors > 0 ? 1 : 0;
    }

    private static void CollectAllReferencedObjects(Repository repo, HashSet<string> reachable)
    {
        var commits = new List<Hash>();

        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            if (refName.StartsWith("refs/heads/"))
            {
                Hash? hash = repo.Refs.GetBranch(refName[11..]);
                if (hash.HasValue) commits.Add(hash.Value);
            }
            else if (refName.StartsWith("refs/tags/"))
            {
                Hash? hash = repo.Refs.GetTag(refName[10..]);
                if (hash.HasValue)
                {
                    TagObject? tagObj = repo.Objects.ReadTag(hash.Value);
                    if (tagObj != null)
                    {
                        reachable.Add(hash.Value.ToHex());
                        commits.Add(tagObj.TargetHash);
                    }
                    else
                    {
                        commits.Add(hash.Value);
                    }
                }
            }
            else if (refName.StartsWith("refs/remotes/"))
            {
                string[] parts = refName[13..].Split('/');
                if (parts.Length >= 2)
                {
                    string remote = parts[0];
                    string branch = string.Join("/", parts.Skip(1));
                    Hash? hash = repo.Refs.GetRemoteBranch(remote, branch);
                    if (hash.HasValue) commits.Add(hash.Value);
                }
            }
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (headCommit.HasValue) commits.Add(headCommit.Value);

        foreach (var stash in repo.Refs.GetStashes())
        {
            commits.Add(stash.CommitHash);
        }

        var processed = new HashSet<string>();
        var queue = new Queue<Hash>();
        foreach (Hash c in commits)
        {
            if (reachable.Add(c.ToHex()))
                queue.Enqueue(c);
        }

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!processed.Add(current.ToHex())) continue;
            reachable.Add(current.ToHex());

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
            }

            // Only attempt to decode a tag when the object really is one: TagObject.Deserialize
            // does not reject non-tag payloads, so reading a commit as a tag yields an empty
            // TargetHash and poisons the traversal.
            var typed = repo.Objects.ReadObjectWithType(current);
            if (typed?.type == ObjectType.Tag)
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

    private static void CheckRefs(Repository repo, ref int errors, ref int warnings, bool verbose)
    {
        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            if (refName == "HEAD") continue;

            Hash? hash = null;
            if (refName.StartsWith("refs/heads/"))
                hash = repo.Refs.GetBranch(refName[11..]);
            else if (refName.StartsWith("refs/tags/"))
                hash = repo.Refs.GetTag(refName[10..]);
            else if (refName.StartsWith("refs/remotes/"))
            {
                string[] parts = refName[13..].Split('/');
                if (parts.Length >= 2)
                    hash = repo.Refs.GetRemoteBranch(parts[0], string.Join("/", parts.Skip(1)));
            }

            if (hash.HasValue)
            {
                if (!repo.Objects.Exists(hash.Value))
                {
                    errors++;
                    Terminal.WriteError($"error: ref '{refName}' points to non-existent object {hash.Value.ToHex()}");
                }
                else if (verbose)
                {
                    Console.WriteLine($"ok: {refName} -> {hash.Value.ToHex()}");
                }
            }
            else
            {
                if (verbose)
                {
                    warnings++;
                    Console.WriteLine($"warning: ref '{refName}' has invalid hash");
                }
            }
        }
    }

    private static void CheckHead(Repository repo, ref int errors, ref int warnings, bool verbose)
    {
        string? currentBranch = repo.Refs.GetCurrentBranch();
        Hash? headCommit = repo.Refs.GetHeadCommit();

        if (headCommit.HasValue && !headCommit.Value.Equals(Hash.Zero))
        {
            if (!repo.Objects.Exists(headCommit.Value))
            {
                errors++;
                Terminal.WriteError($"error: HEAD points to non-existent object {headCommit.Value.ToHex()}");
            }
        }
    }

    private static void CheckIndex(Repository repo, ref int errors, ref int warnings, bool verbose)
    {
        var trackedFiles = repo.Index.GetTrackedFiles();
        foreach (string path in trackedFiles.Keys)
        {
            string fullPath = Path.Combine(repo.RootPath, path);
            if (!File.Exists(fullPath) && verbose)
            {
                warnings++;
                Console.WriteLine($"warning: tracked file '{path}' does not exist in working tree");
            }
        }

        var indexEntries = repo.Index.GetAllEntries();
        foreach (var (key, entry) in indexEntries)
        {
            if (!repo.Objects.Exists(entry.ObjectHash))
            {
                errors++;
                Terminal.WriteError($"error: index entry '{key}' references missing object {entry.ObjectHash.ToHex()}");
            }
        }
    }
}