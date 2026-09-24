namespace SourceManager;

public static class ResetCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool soft = args.GetBoolOption("soft");
        bool mixed = args.GetBoolOption("mixed") || (!soft && !args.GetBoolOption("hard") && !args.GetBoolOption("merge") && !args.GetBoolOption("keep"));
        bool hard = args.GetBoolOption("hard");
        bool merge = args.GetBoolOption("merge");
        bool keep = args.GetBoolOption("keep");
        bool patch = args.GetBoolOption("patch");
        bool quiet = args.GetBoolOption("quiet");

        string? target = args.GetPositional(0);

        Hash targetHash;
        if (target == null || target == "HEAD")
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue || head.Value.Equals(Hash.Zero))
            {
                Terminal.WriteError("fatal: Failed to resolve 'HEAD' as a valid ref.");
                return 1;
            }
            targetHash = head.Value;
        }
        else
        {
            try
            {
                targetHash = RevisionResolver.Resolve(repo, target).Commit;
            }
            catch (RevisionException ex)
            {
                Terminal.WriteError($"fatal: {ex.Message}");
                return 1;
            }
        }

        string? currentBranch = repo.Refs.GetCurrentBranch();
        Hash? oldHead = repo.Refs.GetHeadCommit();

        if (patch)
        {
            Terminal.WriteError("fatal: patch mode not yet implemented");
            return 1;
        }

        if (keep)
        {
            return ResetKeep(repo, targetHash, currentBranch, oldHead, quiet);
        }

        if (merge)
        {
            return ResetMerge(repo, targetHash, currentBranch, oldHead, quiet);
        }

        if (soft)
        {
            return ResetSoft(repo, targetHash, currentBranch, oldHead, quiet);
        }

        if (mixed)
        {
            return ResetMixed(repo, targetHash, currentBranch, oldHead, quiet);
        }

        if (hard)
        {
            return ResetHard(repo, targetHash, currentBranch, oldHead, quiet);
        }

        return 0;
    }

    private static int ResetSoft(Repository repo, Hash targetHash, string? currentBranch, Hash? oldHead, bool quiet)
    {
        if (currentBranch != null)
        {
            repo.Refs.SetBranch(currentBranch, targetHash, $"reset: moving to {targetHash.Short}");
        }
        else
        {
            repo.Refs.SetHeadDetached(targetHash, $"reset: moving to {targetHash.Short}");
        }

        if (!quiet && oldHead.HasValue && !oldHead.Value.Equals(targetHash))
        {
            Terminal.WriteSuccess($"HEAD is now at {targetHash.Short}");
        }
        return 0;
    }

    private static int ResetMixed(Repository repo, Hash targetHash, string? currentBranch, Hash? oldHead, bool quiet)
    {
        if (currentBranch != null)
        {
            repo.Refs.SetBranch(currentBranch, targetHash, $"reset: moving to {targetHash.Short}");
        }
        else
        {
            repo.Refs.SetHeadDetached(targetHash, $"reset: moving to {targetHash.Short}");
        }

        repo.Index.Clear();
        if (targetHash.Equals(Hash.Zero))
        {
            repo.Index.Save();
        }
        else
        {
            Commit? targetCommit = repo.Objects.ReadCommit(targetHash);
            if (targetCommit?.TreeHash.HasValue == true)
            {
                AddTreeToIndex(repo, targetCommit.TreeHash.Value);
            }
            repo.Index.Save();
        }

        if (!quiet && oldHead.HasValue && !oldHead.Value.Equals(targetHash))
        {
            Terminal.WriteSuccess($"HEAD is now at {targetHash.Short}");
        }
        return 0;
    }

    private static int ResetHard(Repository repo, Hash targetHash, string? currentBranch, Hash? oldHead, bool quiet)
    {
        if (currentBranch != null)
        {
            repo.Refs.SetBranch(currentBranch, targetHash, $"reset: moving to {targetHash.Short}");
        }
        else
        {
            repo.Refs.SetHeadDetached(targetHash, $"reset: moving to {targetHash.Short}");
        }

        // Do NOT pre-clear the index: CheckoutCommit needs the pre-reset tracked set to delete
        // files that the target tree does not contain.
        repo.CheckoutCommit(targetHash);
        repo.Index.Save();

        if (!quiet && oldHead.HasValue && !oldHead.Value.Equals(targetHash))
        {
            Terminal.WriteSuccess($"HEAD is now at {targetHash.Short}");
        }
        return 0;
    }

    private static int ResetMerge(Repository repo, Hash targetHash, string? currentBranch, Hash? oldHead, bool quiet)
    {
        var status = repo.GetStatus();
        var confictedFiles = status.Conflicted;

        foreach (string path in confictedFiles)
        {
            string fullPath = Path.Combine(repo.RootPath, path);
            if (File.Exists(fullPath))
                repo.Index.Remove(path);
        }

        ResetMixed(repo, targetHash, currentBranch, oldHead, quiet);

        foreach (string path in confictedFiles)
        {
            string fullPath = Path.Combine(repo.RootPath, path);
            if (File.Exists(fullPath))
            {
                string[] lines = File.ReadAllLines(fullPath);
                var cleanLines = lines
                    .Where(l => !l.StartsWith("<<<<<<<") && !l.StartsWith("=======") && !l.StartsWith(">>>>>>>"))
                    .ToList();
                File.WriteAllLines(fullPath, cleanLines);
            }
        }

        return 0;
    }

    private static int ResetKeep(Repository repo, Hash targetHash, string? currentBranch, Hash? oldHead, bool quiet)
    {
        var status = repo.GetStatus();
        var changedFiles = new Dictionary<string, byte[]>();

        foreach (var (path, kind) in status.Changes)
        {
            string fullPath = Path.Combine(repo.RootPath, path);
            if (File.Exists(fullPath))
            {
                changedFiles[path] = File.ReadAllBytes(fullPath);
            }
        }

        ResetMixed(repo, targetHash, currentBranch, oldHead, quiet);

        foreach (var (path, content) in changedFiles)
        {
            string fullPath = Path.Combine(repo.RootPath, path);
            File.WriteAllBytes(fullPath, content);
        }

        return 0;
    }

    private static void AddTreeToIndex(Repository repo, Hash treeHash, string prefix = "")
    {
        Tree? tree = repo.Objects.ReadTree(treeHash);
        if (tree == null) return;

        foreach (var entry in tree.Entries)
        {
            string path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.Mode == FileMode.Directory)
            {
                AddTreeToIndex(repo, entry.ObjectHash, path);
            }
            else
            {
                repo.Index.Add(path, entry.ObjectHash, entry.Mode);
            }
        }
    }
}