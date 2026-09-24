namespace SourceManager;

public static class BranchCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool list = args.GetBoolOption("list");
        bool showAll = args.GetBoolOption("all");
        bool remote = args.GetBoolOption("remote");
        bool delete = args.GetBoolOption("delete");
        bool forceDelete = args.GetBoolOption("force");
        string? move = args.GetOption("move");
        string? copy = args.GetOption("copy");
        string? setUpstreamTo = args.GetOption("set-upstream-to");
        bool unsetUpstream = args.GetBoolOption("unset-upstream");
        string? merged = args.GetOption("merged");
        string? noMerged = args.GetOption("no-merged");
        bool verbose = args.GetBoolOption("verbose");
        string? sort = args.GetOption("sort");

        string? branchName = args.GetPositional(0);
        string? startPoint = args.GetPositional(1);

        if (delete || forceDelete)
        {
            if (branchName == null)
            {
                Terminal.WriteError("error: branch name required for deletion");
                return 1;
            }
            return DeleteBranch(repo, branchName, forceDelete);
        }

        if (move != null)
        {
            if (branchName == null)
            {
                Terminal.WriteError("error: branch name required for rename");
                return 1;
            }
            return RenameBranch(repo, branchName, move);
        }

        if (copy != null)
        {
            if (branchName == null)
            {
                Terminal.WriteError("error: branch name required for copy");
                return 1;
            }
            return CopyBranch(repo, branchName, copy);
        }

        if (setUpstreamTo != null)
        {
            if (branchName == null) branchName = repo.Refs.GetCurrentBranch();
            if (branchName == null)
            {
                Terminal.WriteError("error: no current branch to set upstream for");
                return 1;
            }
            return SetUpstream(repo, branchName, setUpstreamTo);
        }

        if (unsetUpstream)
        {
            if (branchName == null) branchName = repo.Refs.GetCurrentBranch();
            if (branchName == null)
            {
                Terminal.WriteError("error: no current branch to unset upstream for");
                return 1;
            }
            repo.Config.Unset("branch", $"{branchName}.remote");
            repo.Config.Unset("branch", $"{branchName}.merge");
            return 0;
        }

        if (branchName != null && !list)
        {
            return CreateBranch(repo, branchName, startPoint);
        }

        return ListBranches(repo, showAll, remote, verbose, merged, noMerged, sort);
    }

    private static int ListBranches(Repository repo, bool showAll, bool remoteOnly,
        bool verbose, string? merged, string? noMerged, string? sort)
    {
        string? currentBranch = repo.Refs.GetCurrentBranch();
        var branches = repo.Refs.ListBranches();

        var sortedBranches = sort switch
        {
            "committerdate" => branches.OrderBy(b =>
            {
                Commit? c = repo.Objects.ReadCommit(b.TipHash);
                return c?.Committer.When ?? DateTimeOffset.MinValue;
            }).ToList(),
            "-committerdate" => branches.OrderByDescending(b =>
            {
                Commit? c = repo.Objects.ReadCommit(b.TipHash);
                return c?.Committer.When ?? DateTimeOffset.MinValue;
            }).ToList(),
            _ => branches.OrderBy(b => b.Name).ToList()
        };

        Hash? headCommit = repo.Refs.GetHeadCommit();
        List<string> mergedBranches = new();
        if (merged != null || noMerged != null)
        {
            Hash? targetCommit = merged != null ? ResolveRef(repo, merged) : ResolveRef(repo, noMerged!);
            if (targetCommit.HasValue)
            {
                var ancestors = new HashSet<string>();
                var queue = new Queue<Hash>();
                queue.Enqueue(targetCommit.Value);
                while (queue.Count > 0)
                {
                    Hash current = queue.Dequeue();
                    string hex = current.ToHex();
                    if (!ancestors.Add(hex)) continue;
                    Commit? c = repo.Objects.ReadCommit(current);
                    if (c == null) continue;
                    foreach (var p in c.ParentHashes)
                        queue.Enqueue(p);
                }

                foreach (var b in branches)
                {
                    if (ancestors.Contains(b.TipHash.ToHex()))
                        mergedBranches.Add(b.Name);
                }
            }
        }

        string? pattern = merged != null || noMerged != null ? (merged ?? noMerged) : null;

        foreach (var branch in sortedBranches)
        {
            if (remoteOnly) continue;

            if (merged != null && !mergedBranches.Contains(branch.Name)) continue;
            if (noMerged != null && mergedBranches.Contains(branch.Name)) continue;

            if (branch.IsHead)
                Terminal.Write("* ", Terminal.Color.Green, true);
            else
                Console.Write("  ");

            Terminal.Write(branch.Name, branch.IsHead ? Terminal.Color.Green : Terminal.Color.Default, branch.IsHead);

            if (verbose)
            {
                Console.Write($" {branch.TipHash.Short}");
                Commit? c = repo.Objects.ReadCommit(branch.TipHash);
                if (c != null)
                {
                    string msg = c.Message.Split('\n')[0];
                    Console.Write($" {msg}");
                }
            }

            if (branch.UpstreamBranch != null)
            {
                Terminal.Write($" -> {branch.UpstreamBranch}", Terminal.Color.Cyan);
            }

            Console.WriteLine();
        }

        if (showAll || remoteOnly)
        {
            var remoteBranches = repo.Refs.ListRemoteBranches();
            foreach (var (rn, bn, hash) in remoteBranches.OrderBy(r => r.remote).ThenBy(r => r.branch))
            {
                Console.Write("  ");
                Terminal.WriteLine($"remotes/{rn}/{bn}", Terminal.Color.Red);
                if (verbose)
                {
                    Console.WriteLine($"    {hash.Short}");
                }
            }
        }

        return 0;
    }

    private static int CreateBranch(Repository repo, string branchName, string? startPoint)
    {
        Hash targetHash;
        if (startPoint != null)
        {
            Hash? resolved = ResolveRef(repo, startPoint);
            if (!resolved.HasValue)
            {
                Terminal.WriteError($"fatal: '{startPoint}' is not a valid commit");
                return 1;
            }
            targetHash = resolved.Value;
        }
        else
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit to create branch from");
                return 1;
            }
            targetHash = head.Value;
        }

        try
        {
            repo.Refs.CreateBranch(branchName, targetHash);
            Terminal.WriteSuccess($"Branch '{branchName}' created at {targetHash.Short}");
        }
        catch (InvalidOperationException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static int DeleteBranch(Repository repo, string branchName, bool force)
    {
        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (branchName == currentBranch)
        {
            Terminal.WriteError($"error: Cannot delete branch '{branchName}' checked out at '{repo.RootPath}'");
            return 1;
        }

        Hash? branchHash = repo.Refs.GetBranch(branchName);
        if (!branchHash.HasValue)
        {
            Terminal.WriteError($"error: branch '{branchName}' not found");
            return 1;
        }

        if (!force)
        {
            Hash? headCommit = repo.Refs.GetHeadCommit();
            if (headCommit.HasValue)
            {
                var ancestors = new HashSet<string>();
                var queue = new Queue<Hash>();
                queue.Enqueue(headCommit.Value);
                while (queue.Count > 0)
                {
                    Hash current = queue.Dequeue();
                    if (!ancestors.Add(current.ToHex())) continue;
                    Commit? c = repo.Objects.ReadCommit(current);
                    if (c == null) continue;
                    foreach (var p in c.ParentHashes)
                        queue.Enqueue(p);
                }

                if (!ancestors.Contains(branchHash.Value.ToHex()))
                {
                    Terminal.WriteError($"error: The branch '{branchName}' is not fully merged.");
                    Terminal.WriteError("If you are sure you want to delete it, run 'sm branch -D {branchName}'.");
                    return 1;
                }
            }
        }

        try
        {
            repo.Refs.DeleteBranch(branchName);
            Terminal.WriteSuccess($"Deleted branch {branchName} (was {branchHash.Value.Short}).");
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static int RenameBranch(Repository repo, string oldName, string newName)
    {
        try
        {
            Hash? hash = repo.Refs.GetBranch(oldName);
            if (!hash.HasValue)
            {
                Terminal.WriteError($"error: branch '{oldName}' not found");
                return 1;
            }

            repo.Refs.CreateBranch(newName, hash.Value);
            repo.Refs.DeleteBranch(oldName);

            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch == oldName)
            {
                File.WriteAllText(Path.Combine(repo.SmPath, "HEAD"), $"ref: refs/heads/{newName}\n");
            }

            Terminal.WriteSuccess($"Branch renamed from '{oldName}' to '{newName}'");
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static int CopyBranch(Repository repo, string source, string target)
    {
        try
        {
            Hash? hash = repo.Refs.GetBranch(source);
            if (!hash.HasValue)
            {
                Terminal.WriteError($"error: branch '{source}' not found");
                return 1;
            }

            repo.Refs.CreateBranch(target, hash.Value);
            Terminal.WriteSuccess($"Branch '{target}' created as copy of '{source}' at {hash.Value.Short}");
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static int SetUpstream(Repository repo, string branchName, string upstream)
    {
        string[] parts = upstream.Split('/');
        if (parts.Length < 2)
        {
            Terminal.WriteError($"error: invalid upstream format '{upstream}', expected remote/branch");
            return 1;
        }
        string remote = parts[0];
        string remoteBranch = string.Join("/", parts.Skip(1));
        repo.Refs.SetBranchUpstream(branchName, remote, remoteBranch);
        Terminal.WriteSuccess($"Branch '{branchName}' set up to track remote branch '{upstream}'.");
        return 0;
    }

    private static Hash? ResolveRef(Repository repo, string input)
    {
        if (input == "HEAD")
            return repo.Refs.GetHeadCommit();
        if (Hash.TryParse(input) is Hash h) return h;
        if (input.Length >= 4)
        {
            try { return Helpers.ResolvePartialHash(repo, input); }
            catch { }
        }
        Hash? branch = repo.Refs.GetBranch(input);
        if (branch.HasValue) return branch;
        Hash? tag = repo.Refs.GetTag(input);
        if (tag.HasValue) return tag;
        string[] parts = input.Split('/');
        if (parts.Length >= 2)
        {
            string remote = parts[0];
            string rb = string.Join("/", parts.Skip(1));
            Hash? remoteBranch = repo.Refs.GetRemoteBranch(remote, rb);
            if (remoteBranch.HasValue) return remoteBranch;
        }
        return null;
    }
}