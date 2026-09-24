using System.Text;

namespace SourceManager;

public static class PullCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool noEdit = args.GetBoolOption("no-edit");
        bool rebase = args.GetBoolOption("rebase");
        bool ffOnly = args.GetBoolOption("ff-only");
        bool squash = args.GetBoolOption("squash");
        bool noCommit = args.GetBoolOption("no-commit");
        bool quiet = args.GetBoolOption("quiet");
        bool verbose = args.GetBoolOption("verbose");
        bool all = args.GetBoolOption("all");
        bool commits = args.GetBoolOption("commits");
        bool tags = args.GetBoolOption("tags");
        bool autostash = args.GetBoolOption("autostash");
        string? strategy = args.GetOption("strategy");
        int? depth = args.GetIntOption("depth");

        string? remoteName = args.GetPositional(0);
        string? refspec = args.GetPositional(1);

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (remoteName == null)
        {
            if (currentBranch != null)
                remoteName = repo.Refs.GetBranchRemote(currentBranch);
            if (remoteName == null)
                remoteName = "origin";
        }

        string? remoteUrl = repo.Config.Get("remote", $"{remoteName}.url");
        if (remoteUrl == null)
        {
            Terminal.WriteError($"fatal: No remote repository configured for '{remoteName}'");
            return 1;
        }

        if (!IsSupportedUrl(remoteUrl))
        {
            Terminal.WriteError($"fatal: Unsupported transport protocol.");
            return 1;
        }

        if (!RemoteExists(remoteUrl))
        {
            Terminal.WriteError($"fatal: Remote repository not available at {remoteUrl}");
            return 1;
        }

        if (currentBranch == null)
        {
            Terminal.WriteError("fatal: You are in 'detached HEAD' state. Checkout a branch first.");
            return 1;
        }

        if (autostash)
        {
            var st = repo.GetStatus();
            if (st.Changes.Count > 0 || st.Untracked.Count > 0)
            {
                Terminal.WriteLine("Created autostash");
                var stashEntry = new StashEntry
                {
                    CommitHash = Hash.Zero,
                    Message = "autostash",
                    Author = Signature.Now(repo.Config.GetUserName(), repo.Config.GetUserEmail()),
                    Timestamp = DateTimeOffset.Now
                };
                repo.Refs.AddStash(stashEntry);
            }
        }

        using var transport = Transport.Create(remoteUrl, remoteName);

        string fetchRef = refspec ?? currentBranch;
        Hash? remoteHash = transport.GetBranch(fetchRef);

        if (!remoteHash.HasValue)
        {
            Terminal.WriteError($"fatal: Remote branch '{fetchRef}' not found in {remoteName}");
            return 1;
        }

        Hash? localHash = repo.Refs.GetBranch(currentBranch);
        if (!localHash.HasValue)
        {
            Terminal.WriteError($"fatal: Current branch '{currentBranch}' has no commits");
            return 1;
        }

        var localHaves = new List<Hash>();
        foreach (var branch in repo.Refs.ListBranches())
            if (!branch.TipHash.Equals(Hash.Zero))
                localHaves.Add(branch.TipHash);
        foreach (string tag in repo.Refs.ListTags())
        {
            Hash? th = repo.Refs.GetTag(tag);
            if (th.HasValue) localHaves.Add(th.Value);
        }

        var wants = new List<Hash> { remoteHash.Value };
        var objects = transport.CollectObjects(wants, localHaves);
        foreach (var (hash, (type, data)) in objects)
        {
            WriteObjectLocal(repo, hash, data, type);
        }

        repo.Refs.SetRemoteBranch(remoteName, fetchRef, remoteHash.Value);

        if (localHash.Value.Equals(remoteHash.Value))
        {
            Terminal.WriteLine("Already up to date.");
            return 0;
        }

        string mergeRef = $"{remoteName}/{fetchRef}";

        Hash? mergeBase = repo.Merge.FindMergeBase(localHash.Value, remoteHash.Value);
        // A fast-forward is possible exactly when our tip is an ancestor of theirs.
        bool canFastForward = mergeBase.HasValue && mergeBase.Value.Equals(localHash.Value);

        if (ffOnly && !canFastForward)
        {
            // --ff-only used to short-circuit this whole condition, so a diverged branch was
            // silently force-reset onto the remote tip and local commits were lost.
            Terminal.WriteError("fatal: Not possible to fast-forward, aborting.");
            return 1;
        }

        if (canFastForward && !rebase && !squash)
        {
            repo.CheckoutCommit(remoteHash.Value);
            repo.Refs.SetBranch(currentBranch, remoteHash.Value, $"pull: Fast-forward");
            Terminal.WriteSuccess($"Fast-forward to {remoteHash.Value.Short}");
            return 0;
        }

        if (rebase)
        {
            return RebasePull(repo, currentBranch, localHash.Value, remoteHash.Value, mergeRef);
        }

        if (squash)
        {
            return SquashPull(repo, currentBranch, localHash.Value, remoteHash.Value, mergeRef, noCommit, noEdit);
        }

        var result = repo.Merge.MergeBranches(mergeRef, MergeStrategy.Recursive);
        if (result.HasConflicts)
        {
            Terminal.WriteError("Automatic merge failed; fix conflicts and then commit the result.");
        }
        else if (result.NewHead.HasValue)
        {
            repo.Refs.SetBranch(currentBranch, result.NewHead.Value, $"pull: Merge branch '{mergeRef}'");
            Terminal.WriteSuccess($"Merge made by 'recursive' strategy.");
        }

        return result.HasConflicts ? 1 : 0;
    }

    private static bool IsSupportedUrl(string url)
    {
        return url.StartsWith("file://") || url.StartsWith("sm://") || url.StartsWith("http://") ||
               url.StartsWith("https://") || url.StartsWith("ssh://");
    }

    private static bool RemoteExists(string url)
    {
        using var transport = Transport.Create(url);
        try { return transport.IsRepository(); }
        catch { return false; }
    }

    private static void WriteObjectLocal(Repository repo, Hash hash, byte[] data, ObjectType type)
    {
        // Transport payloads are uncompressed; WriteObjectRaw expects storage-form bytes.
        repo.Objects.WritePayload(hash, type, data);
    }

    private static int RebasePull(Repository repo, string branchName, Hash localHash, Hash remoteHash, string mergeRef)
    {
        Hash? mergeBase = repo.Merge.FindMergeBase(localHash, remoteHash);

        repo.CheckoutCommit(remoteHash);
        repo.Refs.SetBranch(branchName, remoteHash, $"pull: rebase onto {mergeRef}");

        var localCommits = repo.GetCommitHistory();
        var rebaseCommits = new List<Hash>();
        foreach (Hash c in localCommits)
        {
            if (mergeBase.HasValue && c.Equals(mergeBase.Value)) break;
            if (c.Equals(remoteHash)) break;
            rebaseCommits.Add(c);
        }
        rebaseCommits.Reverse();

        foreach (Hash cherryHash in rebaseCommits)
        {
            Commit? cherryCommit = repo.Objects.ReadCommit(cherryHash);
            if (cherryCommit == null) continue;
            if (cherryCommit.ParentHashes.Count == 0) continue;

            Hash? currentHead = repo.Refs.GetHeadCommit();
            if (!currentHead.HasValue) continue;

            var diffs = repo.Diff.DiffCommits(cherryCommit.ParentHashes[0], cherryHash);
            try
            {
                foreach (var diff in diffs)
                {
                    string fullPath = Path.Combine(repo.RootPath, diff.NewPath);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (dir != null) Directory.CreateDirectory(dir);

                    if (diff.Kind == ChangeKind.Added)
                    {
                        byte[]? blobContent = repo.Objects.ReadBlob(diff.NewHash!.Value);
                        if (blobContent != null) File.WriteAllText(fullPath, Encoding.UTF8.GetString(blobContent));
                        repo.StageFile(diff.NewPath);
                    }
                    else if (diff.Kind == ChangeKind.Deleted)
                    {
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        repo.Index.Remove(diff.NewPath);
                    }
                    else
                    {
                        string existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                        string patched = repo.Diff.ApplyPatch(existing, diff.Hunks);
                        File.WriteAllText(fullPath, patched);
                        repo.StageFile(diff.NewPath);
                    }
                }

                Hash treeHash = repo.WriteTreeFromIndex();
                Hash newCommitHash = repo.CreateCommit(treeHash,
                    new List<Hash> { currentHead.Value },
                    cherryCommit.Message);
                repo.Refs.SetBranch(branchName, newCommitHash, $"pull: rebase");
            }
            catch (Exception ex)
            {
                Terminal.WriteError($"error: could not apply {cherryHash.Short}... {cherryCommit.Message.Split('\n')[0]}");
                Terminal.WriteError($"       {ex.Message}");
                return 1;
            }
        }

        Terminal.WriteSuccess($"Successfully rebased onto {remoteHash.Short}.");
        return 0;
    }

    private static int SquashPull(Repository repo, string branchName, Hash localHash, Hash remoteHash,
        string mergeRef, bool noCommit, bool noEdit)
    {
        var diffs = repo.Diff.DiffCommits(localHash, remoteHash);
        foreach (var diff in diffs)
        {
            string fullPath = Path.Combine(repo.RootPath, diff.NewPath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (dir != null) Directory.CreateDirectory(dir);

            if (diff.Kind == ChangeKind.Added)
            {
                byte[]? content = repo.Objects.ReadBlob(diff.NewHash!.Value);
                if (content != null) File.WriteAllText(fullPath, Encoding.UTF8.GetString(content));
                repo.StageFile(diff.NewPath);
            }
            else if (diff.Kind == ChangeKind.Deleted)
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                repo.Index.Remove(diff.NewPath);
            }
            else
            {
                string existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                string patched = repo.Diff.ApplyPatch(existing, diff.Hunks);
                File.WriteAllText(fullPath, patched);
                repo.StageFile(diff.NewPath);
            }
        }

        if (!noCommit)
        {
            string msg = $"Squashed commit of '{mergeRef}'.";
            Hash treeHash = repo.WriteTreeFromIndex();
            var parents = new List<Hash> { localHash, remoteHash };
            Hash newCommitHash = repo.CreateCommit(treeHash, parents, msg);
            repo.Refs.SetBranch(branchName, newCommitHash, $"pull: Squashed commit of '{mergeRef}'");
            Terminal.WriteSuccess($"Squashed into {newCommitHash.Short}");
        }
        else
        {
            Terminal.WriteLine("Pull (squash) completed without commit. Changes are in the working tree.");
        }

        return 0;
    }
}