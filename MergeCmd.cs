namespace SourceManager;

public static class MergeCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        string? strategy = args.GetOption("strategy");
        bool noFf = args.GetBoolOption("no-ff");
        bool ffOnly = args.GetBoolOption("ff-only");
        bool ff = args.GetBoolOption("ff");
        bool squash = args.GetBoolOption("squash");
        bool noCommit = args.GetBoolOption("no-commit");
        bool abort = args.GetBoolOption("abort");
        bool continueMerge = args.GetBoolOption("continue");
        bool quiet = args.GetBoolOption("quiet");
        bool noVerify = args.GetBoolOption("no-verify");
        string? message = args.GetOption("message");

        if (abort)
        {
            return AbortMerge(repo);
        }

        if (continueMerge)
        {
            return ContinueMerge(repo, message);
        }

        string? branchName = args.GetPositional(0);
        if (branchName == null)
        {
            Terminal.WriteError("error: merge requires a branch or commit to merge");
            return 1;
        }

        MergeStrategy mergeStrategy = repo.Config.GetMergeStrategy();
        if (strategy != null)
        {
            mergeStrategy = strategy.ToLowerInvariant() switch
            {
                "recursive" => MergeStrategy.Recursive,
                "resolve" => MergeStrategy.Resolve,
                "ours" => MergeStrategy.Ours,
                "theirs" => MergeStrategy.Theirs,
                "octopus" => MergeStrategy.Octopus,
                "subtree" => MergeStrategy.Subtree,
                "patience" => MergeStrategy.Patience,
                _ => MergeStrategy.Recursive
            };
        }

        if (ffOnly) mergeStrategy = MergeStrategy.FastForward;
        if (noFf && mergeStrategy == MergeStrategy.FastForward)
            mergeStrategy = MergeStrategy.Recursive;

        if (!noVerify && !Hooks.Run(repo, "pre-merge", new[] { branchName }, abortOnFailure: true))
            return 1;

        try
        {
            // --squash and --no-commit both mean "do not create the merge commit here".
            bool deferCommit = squash || noCommit;
            var result = repo.Merge.MergeBranches(branchName, mergeStrategy, deferCommit);

            if (result.IsAlreadyMerged)
            {
                Terminal.WriteLine("Already up to date.");
                return 0;
            }

            if (result.IsFastForward)
            {
                string? currentBranch = repo.Refs.GetCurrentBranch();
                if (currentBranch != null)
                {
                    repo.CheckoutCommit(result.NewHead!.Value);
                    repo.Refs.SetBranch(currentBranch, result.NewHead!.Value, $"merge {branchName}: Fast-forward");
                    if (!quiet)
                        Terminal.WriteSuccess($"Fast-forward merge of '{branchName}'");
                    Hooks.Run(repo, "post-merge", new[] { branchName }, abortOnFailure: false);
                }
                return 0;
            }

            if (result.HasConflicts)
            {
                Terminal.WriteLine("Automatic merge failed; fix conflicts and then commit the result.");
                foreach (var conflict in result.Conflicts)
                {
                    string detail = conflict.HunkCount > 0 ? $" ({conflict.HunkCount} conflicting region(s))" : "";
                    Terminal.WriteLine($"CONFLICT (content): Merge conflict in {conflict.Path}{detail}", Terminal.Color.Red);
                }
                Terminal.WriteLine($"Failed to merge; {result.Conflicts.Count} path(s) left unresolved.", Terminal.Color.Yellow);
                return 1;
            }

            // MergeEngine already created the commit and advanced the branch ref, including its
            // reflog entry. Writing the ref again here duplicated the reflog and let the branch
            // and its reflog disagree.
            if (squash)
            {
                Hash? headCommit = repo.Refs.GetHeadCommit();
                if (!headCommit.HasValue)
                {
                    Terminal.WriteError("fatal: cannot squash onto a branch without commits");
                    return 1;
                }

                string squashMessage = message ?? $"Squash merge of '{branchName}'";
                Hash squashHash = repo.CreateCommit(result.MergedTree!.Value,
                    new List<Hash> { headCommit.Value }, squashMessage);
                repo.UpdateHead(squashHash, $"merge {branchName}: squash");
                repo.Refs.ClearMergeHead();
                if (!quiet)
                    Terminal.WriteSuccess($"Squash merge of '{branchName}' staged and committed as {squashHash.Short}.");
                Hooks.Run(repo, "post-merge", new[] { branchName }, abortOnFailure: false);
                return 0;
            }

            if (noCommit)
            {
                if (!quiet)
                {
                    Terminal.WriteLine($"Automatic merge went well; stopped before committing as requested.");
                    Terminal.WriteLine("Run 'sm commit' to complete the merge.");
                }
                return 0;
            }

            if (!quiet)
                Terminal.WriteSuccess($"Merge '{branchName}' completed successfully.");
            Hooks.Run(repo, "post-merge", new[] { branchName }, abortOnFailure: false);

            return 0;
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: merge failed: {ex.Message}");
            return 1;
        }
    }

    private static int AbortMerge(Repository repo)
    {
        Hash? mergeHead = repo.Refs.GetMergeHead();
        if (mergeHead == null)
        {
            Terminal.WriteError("error: There is no merge to abort (MERGE_HEAD missing).");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        repo.CheckoutCommit(headCommit.Value);
        repo.Refs.ClearMergeHead();
        ConflictState.Clear(repo);
        Terminal.WriteSuccess("Merge aborted.");
        return 0;
    }

    private static int ContinueMerge(Repository repo, string? message)
    {
        Hash? mergeHead = repo.Refs.GetMergeHead();
        if (mergeHead == null)
        {
            Terminal.WriteError("error: There is no merge in progress (MERGE_HEAD missing).");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        string? currentBranch = repo.Refs.GetCurrentBranch();
        string mergeLabel = currentBranch ?? "HEAD";

        if (message == null)
        {
            message = $"Merge branch '{mergeLabel}'";
        }

        Hash treeHash = repo.WriteTreeFromIndex();
        Hash mergeHash = repo.CreateCommit(treeHash, new List<Hash> { headCommit.Value, mergeHead.Value }, message);
        repo.UpdateHead(mergeHash, $"merge {mergeLabel}: Merge made by continuing.");
        repo.Refs.ClearMergeHead();
        ConflictState.Clear(repo);

        Terminal.WriteSuccess("Merge completed.");
        return 0;
    }
}