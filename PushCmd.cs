namespace SourceManager;

public static class PushCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool all = args.GetBoolOption("all");
        bool delete = args.GetBoolOption("delete");
        bool force = args.GetBoolOption("force");
        bool forceWithLease = args.GetBoolOption("force-with-lease");
        bool setUpstream = args.GetBoolOption("set-upstream");
        bool dryRun = args.GetBoolOption("dry-run");
        bool quiet = args.GetBoolOption("quiet");
        bool verbose = args.GetBoolOption("verbose");
        bool progress = args.GetBoolOption("progress");
        bool tags = args.GetBoolOption("tags");
        bool followTags = args.GetBoolOption("follow-tags");
        bool noVerify = args.GetBoolOption("no-verify");
        bool atomic = args.GetBoolOption("atomic");
        bool mirror = args.GetBoolOption("mirror");
        string? pushOption = args.GetOption("push-option");

        string? remoteName = args.GetPositional(0);
        string? refspec = args.GetPositional(1);

        if (remoteName == null)
        {
            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch != null)
            {
                remoteName = repo.Refs.GetBranchRemote(currentBranch);
            }
            if (remoteName == null)
            {
                remoteName = GetDefaultRemote(repo);
            }
            if (remoteName == null)
            {
                Terminal.WriteError("fatal: No configured push destination.");
                Terminal.WriteError("Either specify the URL from the command-line or configure a remote repository");
                return 1;
            }
        }

        string? remoteUrl = repo.Config.Get("remote", $"{remoteName}.url");
        if (remoteUrl == null)
        {
            Terminal.WriteError($"fatal: No configured push URL for remote '{remoteName}'");
            return 1;
        }

        if (!IsSupportedUrl(remoteUrl))
        {
            Terminal.WriteError($"fatal: Unsupported transport protocol. Remote '{remoteUrl}' uses an unsupported scheme.");
            return 1;
        }

        if (!RemoteExists(remoteUrl))
        {
            Terminal.WriteError($"fatal: '{remoteUrl}' does not appear to be a SourceManager repository");
            return 1;
        }

        if (dryRun)
        {
            Terminal.WriteLine("Dry-run: Would push to " + remoteUrl);
        }

        var pushRefs = new List<(string localName, string remoteName, Hash hash, bool force)>();

        if (all)
        {
            foreach (var branch in repo.Refs.ListBranches())
            {
                pushRefs.Add((branch.Name, branch.Name, branch.TipHash, force));
            }
        }
        else if (mirror)
        {
            foreach (var branch in repo.Refs.ListBranches())
                pushRefs.Add((branch.Name, branch.Name, branch.TipHash, true));
            foreach (string tag in repo.Refs.ListTags())
            {
                Hash? tagHash = repo.Refs.GetTag(tag);
                if (tagHash.HasValue)
                    pushRefs.Add(($"refs/tags/{tag}", $"refs/tags/{tag}", tagHash.Value, true));
            }
        }
        else if (delete)
        {
            if (refspec != null)
            {
                pushRefs.Add((refspec, refspec, Hash.Zero, true));
            }
        }
        else if (refspec != null)
        {
            string[] parts = refspec.Split(':');
            string localRef = parts[0];
            string remoteRef = parts.Length > 1 ? parts[1] : localRef;

            Hash? localHash = ResolveRefHash(repo, localRef);
            if (!localHash.HasValue)
            {
                Terminal.WriteError($"fatal: source ref '{localRef}' does not exist");
                return 1;
            }
            pushRefs.Add((localRef, remoteRef, localHash.Value, force));
        }
        else
        {
            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch != null)
            {
                Hash? branchHash = repo.Refs.GetBranch(currentBranch);
                if (!branchHash.HasValue)
                {
                    Terminal.WriteError($"fatal: The current branch {currentBranch} has no upstream branch.");
                    return 1;
                }
                pushRefs.Add((currentBranch, currentBranch, branchHash.Value, force));
            }
            else
            {
                Terminal.WriteError("fatal: You are not currently on a branch.");
                return 1;
            }
        }

        if (tags || followTags)
        {
            foreach (string tag in repo.Refs.ListTags())
            {
                Hash? tagHash = repo.Refs.GetTag(tag);
                if (tagHash.HasValue)
                    pushRefs.Add(($"refs/tags/{tag}", tag, tagHash.Value, force));
            }
        }

        if (!dryRun && !noVerify &&
            !Hooks.Run(repo, "pre-push", new[] { remoteName, remoteUrl }, abortOnFailure: true))
            return 1;

        return PushObjects(repo, remoteName, remoteUrl, pushRefs, dryRun, force, quiet);
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

    private static int PushObjects(Repository repo, string remoteName, string remoteUrl,
        List<(string localName, string remoteName, Hash hash, bool force)> pushRefs,
        bool dryRun, bool force, bool quiet)
    {
        using var transport = Transport.Create(remoteUrl, remoteName);

        foreach (var (localName, remoteName_, refHash, isForce) in pushRefs)
        {
            if (refHash.Equals(Hash.Zero))
            {
                if (!dryRun)
                {
                    transport.DeleteBranch(remoteName_);
                }
                Terminal.WriteLine($" - [deleted]          {remoteName_}");
                continue;
            }

            bool isTag = localName.StartsWith("refs/tags/");

            if (!isForce && !isTag)
            {
                Hash? remoteHash = transport.GetBranch(remoteName_);
                // Fast-forward means the remote tip is an ANCESTOR of the commit being pushed.
                // This must be answered with the LOCAL object graph: the new commits are not on
                // the remote yet, so asking the remote to walk from them finds nothing and
                // rejects every ordinary fast-forward push.
                if (remoteHash.HasValue && !repo.IsAncestor(remoteHash.Value, refHash))
                {
                    Terminal.WriteError($" ! [rejected]         {remoteName_} -> {remoteName_} (non-fast-forward)");
                    Terminal.WriteError("error: failed to push some refs");
                    Terminal.WriteError("hint: Updates were rejected because the remote contains work that you do not have");
                    Terminal.WriteError("hint: locally. Use 'sm pull' before pushing again.");
                    return 1;
                }
            }

            if (!dryRun)
            {
                var remoteHavesCandidates = new List<Hash>();
                foreach (var branch in transport.ListBranches())
                {
                    if (!branch.TipHash.Equals(Hash.Zero))
                        remoteHavesCandidates.Add(branch.TipHash);
                }
                foreach (string tag in transport.ListTags())
                {
                    Hash? th = transport.GetTag(tag);
                    if (th.HasValue) remoteHavesCandidates.Add(th.Value);
                }

                var allLocalWants = new List<Hash> { refHash };

                // CollectObjectsLocal now walks each commit's full tree recursively, so a
                // second pass for "additional tree objects" is no longer needed.
                var localObjects = CollectObjectsLocal(repo, allLocalWants, remoteHavesCandidates);
                var batch = new Dictionary<Hash, (ObjectType type, byte[] data)>();
                foreach (var (hash, (type, data)) in localObjects)
                {
                    batch[hash] = (type, data);
                }
                transport.WriteObjectsBatch(batch);
            }

            if (!dryRun)
            {
                if (isTag)
                {
                    transport.SetTag(remoteName_, refHash);
                }
                else
                {
                    // The remote must receive refs/heads/<branch>. SetRemoteBranch writes the
                    // remote-tracking namespace (refs/remotes/<remote>/<branch>), which lives on
                    // the LOCAL side of a fetch — using it here pushed the branch to a ref that
                    // the remote could never resolve, so `fetch` from that remote saw nothing.
                    transport.SetBranch(remoteName_, refHash);
                }
            }

            if (!quiet)
            {
                string arrow = localName == remoteName_ ? "" : $" ({localName} -> {remoteName_})";
                Terminal.WriteLine($"   {refHash.Short}  {remoteName_}");
            }
        }

        if (!quiet)
        {
            Console.WriteLine();
            Terminal.WriteSuccess("Push completed successfully.");
        }
        return 0;
    }

    /// <summary>
    /// Collects everything needed to make <paramref name="wants"/> readable on the remote:
    /// the commits, ALL of their trees (recursively), and every blob they reference.
    ///
    /// Previously only the tip commit's tree was walked, so pushing a branch with more than one
    /// new commit left every ancestor's tree missing on the remote — the remote then could not
    /// check out or walk its own history.
    /// </summary>
    private static Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjectsLocal(Repository repo,
        List<Hash> wants, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();
        var haveSet = new HashSet<string>(haves.Select(h => h.ToHex()));
        var visited = new HashSet<string>();
        var queue = new Queue<Hash>(wants);

        void EnqueueTree(Hash treeHash)
        {
            var trees = new Stack<Hash>();
            trees.Push(treeHash);
            while (trees.Count > 0)
            {
                Hash current = trees.Pop();
                string hex = current.ToHex();
                if (!visited.Add(hex)) continue;
                if (haveSet.Contains(hex)) continue;

                var obj = repo.Objects.ReadObjectWithType(current);
                if (!obj.HasValue) continue;
                result[current] = (obj.Value.type, obj.Value.data);

                if (obj.Value.type != ObjectType.Tree) continue;
                Tree? tree = ObjectStore.DeserializeTree(obj.Value.data);
                if (tree == null) continue;
                foreach (var entry in tree.Entries)
                {
                    string entryHex = entry.ObjectHash.ToHex();
                    if (visited.Contains(entryHex) || haveSet.Contains(entryHex)) continue;

                    if (entry.Mode == FileMode.Directory)
                    {
                        trees.Push(entry.ObjectHash);
                    }
                    else
                    {
                        var blob = repo.Objects.ReadObjectWithType(entry.ObjectHash);
                        if (blob.HasValue)
                        {
                            result[entry.ObjectHash] = (blob.Value.type, blob.Value.data);
                            visited.Add(entryHex);
                        }
                    }
                }
            }
        }

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = repo.Objects.ReadObjectWithType(current);
            if (!obj.HasValue) continue;

            result[current] = (obj.Value.type, obj.Value.data);

            if (obj.Value.type == ObjectType.Commit)
            {
                Commit? commit = ObjectStore.DeserializeCommit(obj.Value.data);
                if (commit == null) continue;
                if (commit.TreeHash.HasValue)
                    EnqueueTree(commit.TreeHash.Value);
                foreach (Hash p in commit.ParentHashes)
                    queue.Enqueue(p);
            }
            else if (obj.Value.type == ObjectType.Tag)
            {
                TagObject? tag = ObjectStore.DeserializeTag(obj.Value.data);
                if (tag != null)
                    queue.Enqueue(tag.TargetHash);
            }
        }

        return result;
    }

    private static Dictionary<Hash, (ObjectType type, byte[] data)> CollectTreeObjectsLocal(Repository repo,
        Hash commitHash, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();
        var haveSet = new HashSet<string>(haves.Select(h => h.ToHex()));
        var visited = new HashSet<string>();

        Commit? commit = repo.Objects.ReadCommit(commitHash);
        if (commit?.TreeHash.HasValue != true) return result;

        var queue = new Queue<Hash>();
        queue.Enqueue(commit.TreeHash.Value);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = repo.Objects.ReadObjectWithType(current);
            if (!obj.HasValue) continue;

            result[current] = (obj.Value.type, obj.Value.data);

            if (obj.Value.type == ObjectType.Tree)
            {
                Tree? tree = ObjectStore.DeserializeTree(obj.Value.data);
                if (tree != null)
                {
                    foreach (var entry in tree.Entries)
                    {
                        string entryHex = entry.ObjectHash.ToHex();
                        if (!visited.Contains(entryHex) && !haveSet.Contains(entryHex))
                        {
                            var blobObj = repo.Objects.ReadObjectWithType(entry.ObjectHash);
                            if (blobObj.HasValue)
                            {
                                result[entry.ObjectHash] = (blobObj.Value.type, blobObj.Value.data);
                                visited.Add(entryHex);
                            }
                        }
                        if (entry.Mode == FileMode.Directory)
                            queue.Enqueue(entry.ObjectHash);
                    }
                }
            }
        }

        return result;
    }

    private static Hash? ResolveRefHash(Repository repo, string input)
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
        return null;
    }

    private static string? GetDefaultRemote(Repository repo)
    {
        var remotes = new HashSet<string>();
        foreach (string section in repo.Config.GetSections())
        {
            if (section == "remote")
            {
                foreach (var (key, _) in repo.Config.GetSection(section))
                {
                    int dotIdx = key.IndexOf('.');
                    if (dotIdx >= 0)
                        remotes.Add(key[..dotIdx]);
                }
            }
        }
        return remotes.Count == 1 ? remotes.First() : remotes.FirstOrDefault(r => r == "origin");
    }
}