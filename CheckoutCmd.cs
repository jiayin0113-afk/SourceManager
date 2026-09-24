namespace SourceManager;

public static class CheckoutCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        string? newBranch = args.GetOption("branch");
        bool force = args.GetBoolOption("force");
        bool merge = args.GetBoolOption("merge");
        bool ours = args.GetBoolOption("ours");
        bool theirs = args.GetBoolOption("theirs");
        string? orphan = args.GetOption("orphan");
        bool detach = args.GetBoolOption("detach");
        bool track = args.GetBoolOption("track");
        bool noTrack = args.GetBoolOption("no-track");

        string? target = args.GetPositional(0);

        if (orphan != null)
        {
            return CheckoutOrphan(repo, orphan);
        }

        if (newBranch != null)
        {
            return CheckoutNewBranch(repo, newBranch, target, track, noTrack);
        }

        if (target == null)
        {
            Terminal.WriteError("error: you must specify a branch or commit to checkout");
            return 1;
        }

        if (target == "-")
        {
            return CheckoutPrevious(repo);
        }

        if (detach)
        {
            return CheckoutDetached(repo, target);
        }

        Hash? branchHash = repo.Refs.GetBranch(target);
        if (branchHash.HasValue)
        {
            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch == target)
            {
                // Already on this branch, but the working tree may not match it — a fresh fetch,
                // a manual delete, or a bare-ish state. Materialise the branch instead of
                // reporting success while leaving files missing.
                var status = repo.GetStatus();
                bool matchesBranch = status.Staged.Count == 0 && status.Changes.Count == 0;
                if (matchesBranch)
                {
                    Terminal.WriteLine($"Already on '{target}'");
                    return 0;
                }

                if (!force && status.Changes.Count > 0)
                {
                    Terminal.WriteError("error: Your local changes to the following files would be overwritten by checkout:");
                    foreach (var (path, _) in status.Changes)
                        Terminal.WriteError($"    {path}");
                    Terminal.WriteError("Please commit your changes or stash them before you switch branches.");
                    return 1;
                }

                return SwitchToBranch(repo, target, branchHash.Value);
            }

            if (!force)
            {
                var status = repo.GetStatus();
                if (status.Changes.Count > 0)
                {
                    Terminal.WriteError("error: Your local changes to the following files would be overwritten by checkout:");
                    foreach (var (path, _) in status.Changes)
                        Terminal.WriteError($"    {path}");
                    Terminal.WriteError("Please commit your changes or stash them before you switch branches.");
                    return 1;
                }
            }

            return SwitchToBranch(repo, target, branchHash.Value);
        }

        return CheckoutDetached(repo, target);
    }

    private static int SwitchToBranch(Repository repo, string branchName, Hash commitHash)
    {
        repo.CheckoutCommit(commitHash);
        repo.Refs.SetHeadBranch(branchName);
        Terminal.WriteSuccess($"Switched to branch '{branchName}'");
        return 0;
    }

    private static int CheckoutNewBranch(Repository repo, string branchName, string? startPoint, bool track, bool noTrack)
    {
        Hash startHash;
        if (startPoint != null)
        {
            startHash = ResolveHash(repo, startPoint);
        }
        else
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit to branch from");
                return 1;
            }
            startHash = head.Value;
        }

        try
        {
            repo.Refs.CreateBranch(branchName, startHash);
        }
        catch (InvalidOperationException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        repo.CheckoutCommit(startHash);
        repo.Refs.SetHeadBranch(branchName);

        if (track && startPoint != null)
        {
            string[] parts = startPoint.Split('/');
            if (parts.Length >= 2)
            {
                repo.Refs.SetBranchUpstream(branchName, parts[0], string.Join("/", parts.Skip(1)));
            }
        }

        Terminal.WriteSuccess($"Switched to a new branch '{branchName}'");
        return 0;
    }

    private static int CheckoutDetached(Repository repo, string target)
    {
        Hash targetHash = ResolveHash(repo, target);
        repo.CheckoutCommit(targetHash);
        repo.Refs.SetHeadDetached(targetHash);
        Terminal.WriteSuccess($"HEAD is now at {targetHash.Short}");
        Terminal.WriteLine("You are in 'detached HEAD' state. You can look around, make experimental");
        Terminal.WriteLine("changes and commit them, and you can discard any commits you make in this");
        Terminal.WriteLine("state without impacting any branches by switching back to a branch.");
        return 0;
    }

    private static int CheckoutOrphan(Repository repo, string branchName)
    {
        try
        {
            repo.Refs.CreateBranch(branchName, Hash.Zero);
        }
        catch (InvalidOperationException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        repo.Index.Clear();
        repo.Index.Save();
        repo.Refs.SetHeadBranch(branchName);
        Terminal.WriteSuccess($"Switched to a new orphan branch '{branchName}'");
        return 0;
    }

    private static int CheckoutPrevious(Repository repo)
    {
        // Newest-first. The branch to return to is the "from" of the most recent checkout, not
        // some older entry — scanning from the end picked the wrong branch ("checkout -" was a
        // no-op that re-ran the previous switch).
        var reflog = repo.Refs.GetReflog("HEAD", 0);

        Hash? prevCommit = null;
        string? prevRef = null;
        foreach (var entry in reflog)
        {
            if (entry.Message.StartsWith("checkout: moving from", StringComparison.Ordinal))
            {
                string msg = entry.Message;
                int fromIdx = msg.IndexOf("from ", StringComparison.Ordinal) + 5;
                int toIdx = msg.IndexOf(" to ", StringComparison.Ordinal);
                if (fromIdx > 4 && toIdx > fromIdx)
                    prevRef = msg[fromIdx..toIdx];
                prevCommit = entry.OldHash;
                break;
            }
        }

        if (!prevCommit.HasValue || prevCommit.Value.Equals(Hash.Zero))
        {
            Terminal.WriteError("error: no previous branch to switch to");
            return 1;
        }

        // "from" names a branch only while that branch still exists; a detached short hash is not
        // a branch, so it falls back to a detached checkout.
        if (prevRef != null && repo.Refs.GetBranch(prevRef) is Hash branchTip)
            return SwitchToBranch(repo, prevRef, branchTip);

        return CheckoutDetached(repo, prevCommit.Value.ToHex());
    }

    private static Hash ResolveHash(Repository repo, string input)
    {
        if (input == "HEAD")
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue)
                throw new InvalidOperationException("fatal: HEAD does not exist");
            return head.Value;
        }
        if (Hash.TryParse(input) is Hash h) return h;
        if (input.Length >= 4)
        {
            try { return Helpers.ResolvePartialHash(repo, input); }
            catch (ArgumentException ex) { throw; }
        }
        Hash? branch = repo.Refs.GetBranch(input);
        if (branch.HasValue) return branch.Value;
        Hash? tag = repo.Refs.GetTag(input);
        if (tag.HasValue) return tag.Value;
        throw new InvalidOperationException($"fatal: '{input}' is not a valid revision");
    }
}