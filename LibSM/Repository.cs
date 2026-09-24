namespace SourceManager;

public class Repository
{
    public string RootPath { get; }
    public string SmPath { get; }
    public ObjectStore Objects { get; }
    public Index Index { get; }
    public RefStore Refs { get; }
    public ConfigStore Config { get; }
    public IgnoreMatcher Ignore { get; }
    public DiffEngine Diff { get; }
    public MergeEngine Merge { get; }

    public Repository(string rootPath, bool bare = false)
    {
        RootPath = Path.GetFullPath(rootPath);
        SmPath = bare ? RootPath : Path.Combine(RootPath, ".sm");
        Objects = new ObjectStore(this);
        Index = new Index(RootPath);
        Refs = new RefStore(this);
        Config = new ConfigStore(RootPath);
        Ignore = new IgnoreMatcher(RootPath);
        Ignore.LoadRules();
        Diff = new DiffEngine(this);
        Merge = new MergeEngine(this);
    }

    /// <summary>
    /// Opens a repository at <paramref name="path"/>, accepting either layout:
    /// a working repository (<c>&lt;path&gt;/.sm</c>) or a bare repository, where the git
    /// directory IS <paramref name="path"/>.
    /// </summary>
    public static Repository Open(string path)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(Path.Combine(full, ".sm")) && File.Exists(Path.Combine(full, ".sm", "HEAD")))
            return new Repository(full);
        if (File.Exists(Path.Combine(full, "HEAD")) && Directory.Exists(Path.Combine(full, "objects")))
            return new Repository(full, bare: true);
        throw new InvalidOperationException($"Not a repository: {full}");
    }

    /// <summary>True when <paramref name="path"/> is a working or bare repository.</summary>
    public static bool IsAnyRepository(string path)
    {
        string full = Path.GetFullPath(path);
        return (Directory.Exists(Path.Combine(full, ".sm")) && File.Exists(Path.Combine(full, ".sm", "HEAD")))
            || (File.Exists(Path.Combine(full, "HEAD")) && Directory.Exists(Path.Combine(full, "objects")));
    }

    public static Repository? Find(string? startPath = null)
    {
        string? current = startPath ?? Environment.CurrentDirectory;
        while (current != null)
        {
            string smPath = Path.Combine(current, ".sm");
            if (Directory.Exists(smPath) && File.Exists(Path.Combine(smPath, "HEAD")))
                return new Repository(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
        return null;
    }

    public static Repository FindRequired(string? startPath = null)
    {
        return Find(startPath) ?? throw new InvalidOperationException("Not a SourceManager repository (or any parent up to root). Run 'sm init' first.");
    }

    public static bool IsRepository(string path)
    {
        return IsAnyRepository(path);
    }

    /// <summary>
    /// Creates the repository layout. For a bare repository the git directory IS
    /// <see cref="RootPath"/>, so the "already initialised" test and the created paths differ.
    /// Previously --bare still created a <c>.sm</c> subtree and printed "(bare)", producing a
    /// directory that no transport could open.
    /// </summary>
    public void Init(string? initialBranch = null, bool bare = false)
    {
        string gitDir = SmPath;
        if (bare)
        {
            if (File.Exists(Path.Combine(gitDir, "HEAD")) && Directory.Exists(Path.Combine(gitDir, "objects")))
                throw new InvalidOperationException($"Already a SourceManager repository: {RootPath}");
        }
        else if (Directory.Exists(gitDir))
        {
            throw new InvalidOperationException($"Already a SourceManager repository: {RootPath}");
        }

        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(Path.Combine(gitDir, "objects"));
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "tags"));
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "remotes"));
        Directory.CreateDirectory(Path.Combine(gitDir, "logs"));
        Directory.CreateDirectory(Path.Combine(gitDir, "hooks"));
        Directory.CreateDirectory(Path.Combine(gitDir, "info"));

        string branch = initialBranch ?? Config.GetDefaultBranch();
        Refs.SetHeadBranch(branch);

        SaveDescription();
        SaveExclude();
    }

    private void SaveDescription()
    {
        string path = Path.Combine(SmPath, "description");
        File.WriteAllText(path, "Unnamed repository; edit this file to name it.\n");
    }

    private void SaveExclude()
    {
        string infoPath = Path.Combine(SmPath, "info");
        string excludePath = Path.Combine(infoPath, "exclude");
        PathUtil.EnsureDirectory(infoPath);
        File.WriteAllText(excludePath, "# sm exclude file (local, not shared)\n");
    }

    /// <summary>
    /// Computes the full three-way status.
    ///
    /// The distinction that matters: <c>Changes</c> is working-tree vs index (unstaged work)
    /// and <c>Staged</c> is index vs HEAD (what a commit would record). Comparing only the
    /// working tree against the index — as this method used to — makes a staged change look
    /// unstaged and makes a fully staged file report as clean.
    /// </summary>
    public WorkingTreeStatus GetStatus()
    {
        var status = new WorkingTreeStatus
        {
            CurrentBranch = Refs.GetCurrentBranch() ?? "(detached)",
        };

        var trackedFiles = Index.GetTrackedFiles();

        Dictionary<string, Hash>? headTree = null;
        Hash? headCommit = Refs.GetHeadCommit();
        if (headCommit.HasValue)
        {
            Commit? commit = Objects.ReadCommit(headCommit.Value);
            if (commit?.TreeHash.HasValue == true)
                headTree = GetTreeEntries(commit.TreeHash.Value);
        }

        // ── Working tree vs index ───────────────────────────────────────────────
        foreach (var (path, hash) in trackedFiles)
        {
            string fullPath = Path.Combine(RootPath, path);
            if (!File.Exists(fullPath))
            {
                status.Changes[path] = ChangeKind.Deleted;
                continue;
            }

            if (!HashUtil.HashFile(fullPath).Equals(hash))
                status.Changes[path] = ChangeKind.Modified;
        }

        var workTreeFiles = new HashSet<string>(
            PathUtil.EnumerateAllFiles(RootPath, Ignore).Select(f => PathUtil.GetRelativePath(RootPath, f))
        );

        foreach (string path in workTreeFiles)
        {
            if (!trackedFiles.ContainsKey(path))
                status.Untracked.Add(path);
        }

        // ── Index vs HEAD ───────────────────────────────────────────────────────
        if (headTree != null)
        {
            foreach (var (path, indexHash) in trackedFiles)
            {
                if (!headTree.TryGetValue(path, out Hash headHash))
                    status.Staged[path] = ChangeKind.Added;
                else if (!indexHash.Equals(headHash))
                    status.Staged[path] = ChangeKind.Modified;
            }

            foreach (var (path, _) in headTree)
            {
                if (!trackedFiles.ContainsKey(path))
                    status.Staged[path] = ChangeKind.Deleted;
            }
        }
        else
        {
            // No HEAD yet: everything in the index is a new file.
            foreach (var (path, _) in trackedFiles)
                status.Staged[path] = ChangeKind.Added;
        }

        // A path removed from the index while still on disk (rm --cached) is a staged deletion
        // PLUS an untracked file — that is "D " with "??", not "DA". Reporting it as an unstaged
        // addition claimed the work tree disagreed with the index about content it no longer
        // tracks at all.

        // A merge that stopped on conflicts is real repository state; surface it so `status`,
        // `commit` and `reset --merge` can all see which paths are unresolved.
        if (Refs.GetMergeHead().HasValue || ConflictState.Exists(this))
        {
            foreach (string path in ConflictState.GetUnresolved(this))
                status.Conflicted.Add(path);
        }

        return status;
    }

    public Dictionary<string, Hash> GetTreeEntries(Hash treeHash, string prefix = "")
    {
        var result = new Dictionary<string, Hash>();
        Tree? tree = Objects.ReadTree(treeHash);
        if (tree == null) return result;

        foreach (var entry in tree.Entries)
        {
            string path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.Mode == FileMode.Directory)
            {
                var subEntries = GetTreeEntries(entry.ObjectHash, path);
                foreach (var (k, v) in subEntries)
                    result[k] = v;
            }
            else
            {
                result[path] = entry.ObjectHash;
            }
        }
        return result;
    }

    public Hash WriteTreeFromIndex()
    {
        var entries = Index.Entries
            .Where(e => !e.IntentToAdd)
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        return WriteTreeLevel(entries, 0);
    }

    /// <summary>
    /// Builds and persists the tree for the directory level starting at <paramref name="prefixLength"/>
    /// characters into each path. Entries are fully ordered, so every path that shares a directory
    /// segment is contiguous and the subtree can be closed as soon as the segment changes.
    /// </summary>
    private Hash WriteTreeLevel(List<IndexEntry> entries, int prefixLength)
    {
        var tree = new Tree();
        int i = 0;
        while (i < entries.Count)
        {
            string relative = entries[i].Path.Substring(prefixLength);
            int slash = relative.IndexOf('/');
            if (slash < 0)
            {
                tree.Entries.Add(new TreeEntry
                {
                    Mode = entries[i].Mode,
                    Name = relative,
                    ObjectHash = entries[i].ObjectHash
                });
                i++;
                continue;
            }

            string dirName = relative[..slash];
            int nextSegment = prefixLength + slash + 1;
            int j = i;
            while (j < entries.Count &&
                   entries[j].Path.Length > nextSegment &&
                   entries[j].Path.AsSpan(prefixLength).StartsWith(dirName.AsSpan()) &&
                   entries[j].Path[prefixLength + dirName.Length] == '/')
            {
                j++;
            }

            Hash subHash = WriteTreeLevel(entries.GetRange(i, j - i), nextSegment);
            tree.Entries.Add(new TreeEntry
            {
                Mode = FileMode.Directory,
                Name = dirName,
                ObjectHash = subHash
            });
            i = j;
        }

        return Objects.WriteTree(tree);
    }

    public Hash CreateCommit(Hash treeHash, List<Hash> parents, string message)
    {
        var commit = new Commit
        {
            TreeHash = treeHash,
            ParentHashes = parents,
            Author = Signature.Now(Config.GetUserName(), Config.GetUserEmail()),
            Committer = Signature.Now(Config.GetUserName(), Config.GetUserEmail()),
            Message = message
        };
        return Objects.WriteCommit(commit);
    }

    public void UpdateHead(Hash commitHash, string? message = null)
    {
        string? branch = Refs.GetCurrentBranch();
        if (branch != null)
        {
            // SetBranch records both the branch ref and (because it is checked out) HEAD.
            Refs.SetBranch(branch, commitHash, message);
        }
        else
        {
            // SetHeadDetached records the HEAD move itself.
            Refs.SetHeadDetached(commitHash, message ?? "commit (detached)");
        }
    }

    public Dictionary<Hash, CommitGraphNode> BuildCommitGraph()
    {
        var graph = new Dictionary<Hash, CommitGraphNode>();
        var visited = new HashSet<Hash>();
        var queue = new Queue<Hash>();

        foreach (var branch in Refs.ListBranches())
        {
            if (!visited.Contains(branch.TipHash))
                queue.Enqueue(branch.TipHash);
        }

        string? headBranch = Refs.GetCurrentBranch();
        Hash? headCommit = Refs.GetHeadCommit();
        if (headCommit.HasValue && !visited.Contains(headCommit.Value))
            queue.Enqueue(headCommit.Value);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!visited.Add(current)) continue;

            Commit? commit = Objects.ReadCommit(current);
            if (commit == null) continue;

            var node = new CommitGraphNode
            {
                CommitHash = current,
                ParentHashes = commit.ParentHashes
            };
            graph[current] = node;

            foreach (var parent in commit.ParentHashes)
            {
                if (!visited.Contains(parent))
                    queue.Enqueue(parent);

                if (graph.TryGetValue(parent, out var parentNode))
                {
                    if (!parentNode.ChildHashes.Contains(current))
                        parentNode.ChildHashes.Add(current);
                }
            }
        }

        return graph;
    }

    /// <summary>
    /// Walks history from <paramref name="startFrom"/> (default HEAD) newest-first.
    ///
    /// When <paramref name="path"/> is given only commits that CHANGED that path are returned —
    /// comparing the commit's tree against its first parent's. Testing mere presence made
    /// `sm log -- &lt;path&gt;` list every commit that still contained the file.
    /// When <paramref name="pathIsPrefix"/> is true the path is treated as a directory prefix.
    /// </summary>
    public List<Hash> GetCommitHistory(Hash? startFrom = null, int maxCount = 100, string? path = null,
        bool pathIsPrefix = false)
    {
        var history = new List<Hash>();
        var visited = new HashSet<Hash>();
        var queue = new Queue<Hash>();

        Hash? start = startFrom ?? Refs.GetHeadCommit();
        if (!start.HasValue) return history;

        queue.Enqueue(start.Value);

        while (queue.Count > 0 && history.Count < maxCount)
        {
            Hash current = queue.Dequeue();
            if (!visited.Add(current)) continue;

            Commit? commit = Objects.ReadCommit(current);
            if (commit == null) continue;

            if (path != null && !CommitTouchesPath(commit, path, pathIsPrefix))
            {
                // Still traverse parents: a commit that did not touch the path may sit between
                // the tip and older commits that did.
                foreach (var parent in commit.ParentHashes)
                {
                    if (!visited.Contains(parent))
                        queue.Enqueue(parent);
                }
                continue;
            }

            history.Add(current);

            foreach (var parent in commit.ParentHashes)
            {
                if (!visited.Contains(parent))
                    queue.Enqueue(parent);
            }
        }

        return history;
    }

    /// <summary>True when the commit's tree differs from its first parent's tree at <paramref name="path"/>.</summary>
    private bool CommitTouchesPath(Commit commit, string path, bool pathIsPrefix)
    {
        if (commit.TreeHash.HasValue)
        {
            var files = GetTreeEntries(commit.TreeHash.Value);
            if (!TreeMatchesPath(files, path, pathIsPrefix)) return false;
        }

        if (commit.ParentHashes.Count == 0)
            return commit.TreeHash.HasValue;

        Commit? parent = Objects.ReadCommit(commit.ParentHashes[0]);
        if (parent?.TreeHash.HasValue != true)
            return true;

        var parentFiles = GetTreeEntries(parent.TreeHash.Value);
        var commitFiles = commit.TreeHash.HasValue ? GetTreeEntries(commit.TreeHash.Value) : new Dictionary<string, Hash>();

        foreach (var (p, hash) in commitFiles)
        {
            if (!MatchesPath(p, path, pathIsPrefix)) continue;
            if (!parentFiles.TryGetValue(p, out Hash oldHash) || !oldHash.Equals(hash))
                return true;
        }
        foreach (var (p, _) in parentFiles)
        {
            if (!MatchesPath(p, path, pathIsPrefix)) continue;
            if (!commitFiles.ContainsKey(p)) return true;
        }
        return false;
    }

    private static bool MatchesPath(string candidate, string path, bool prefix)
        => prefix
            ? candidate.Equals(path, StringComparison.Ordinal) || candidate.StartsWith(path + "/", StringComparison.Ordinal)
            : candidate.Equals(path, StringComparison.Ordinal);

    private static bool TreeMatchesPath(Dictionary<string, Hash> files, string path, bool prefix)
    {
        foreach (string p in files.Keys)
        {
            if (MatchesPath(p, path, prefix)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> by
    /// walking parents in THIS repository. Callers must use the repository that owns
    /// <paramref name="descendant"/>: asking a remote whether it already contains a commit that
    /// has not been pushed yet always answers "no".
    /// </summary>
    public bool IsAncestor(Hash ancestor, Hash descendant)
    {
        var visited = new HashSet<Hash>();
        var queue = new Queue<Hash>();
        queue.Enqueue(descendant);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current.Equals(ancestor)) return true;

            Commit? commit = Objects.ReadCommit(current);
            if (commit == null) continue;
            foreach (var parent in commit.ParentHashes)
            {
                if (!visited.Contains(parent))
                    queue.Enqueue(parent);
            }
        }
        return false;
    }

    public Hash? FindMergeBase(Hash a, Hash b)
    {
        var ancestorsA = new HashSet<Hash>();
        var queue = new Queue<Hash>();
        queue.Enqueue(a);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!ancestorsA.Add(current)) continue;
            Commit? commit = Objects.ReadCommit(current);
            if (commit == null) continue;
            foreach (var parent in commit.ParentHashes)
                queue.Enqueue(parent);
        }

        queue.Clear();
        queue.Enqueue(b);
        var visited = new HashSet<Hash>();

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (ancestorsA.Contains(current))
                return current;
            Commit? commit = Objects.ReadCommit(current);
            if (commit == null) continue;
            foreach (var parent in commit.ParentHashes)
                queue.Enqueue(parent);
        }

        return null;
    }

    /// <summary>
    /// Replaces the working tree and index with the given commit's tree.
    ///
    /// Files that were tracked before but are absent from the target tree are DELETED. Writing
    /// only the target's files left stale paths behind, so a branch switch or `reset --hard`
    /// appeared to work while leaving files from the previous state on disk.
    /// </summary>
    public void CheckoutCommit(Hash commitHash, IReadOnlyCollection<string>? removePaths = null)
    {
        Commit? commit = Objects.ReadCommit(commitHash);
        if (commit == null)
            throw new InvalidOperationException($"Commit not found: {commitHash.Short}");
        if (!commit.TreeHash.HasValue)
            throw new InvalidOperationException("Commit has no tree");

        var target = GetTreeEntries(commit.TreeHash.Value);

        // Callers that already cleared the index (reset --hard) must name the paths to drop;
        // otherwise the set of "files that existed before" is gone and stale files survive.
        var previous = removePaths ?? Index.GetTrackedFiles().Keys.ToList();

        using var lockFile = AcquireLock();

        Index.Clear();

        foreach (string path in previous)
        {
            if (target.ContainsKey(path)) continue;
            string fullPath = Path.Combine(RootPath, path);
            if (File.Exists(fullPath))
            {
                try { File.Delete(fullPath); } catch { }
            }
        }

        CheckoutTree(commit.TreeHash.Value);
        Index.Save();
        PruneEmptyDirectories();
    }

    /// <summary>Removes directories left empty by a checkout; never touches the repo root.</summary>
    private void PruneEmptyDirectories()
    {
        void Walk(string dir)
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(sub) == ".sm") continue;
                Walk(sub);
                if (Directory.GetFileSystemEntries(sub).Length == 0)
                {
                    try { Directory.Delete(sub); } catch { }
                }
            }
        }
        Walk(RootPath);
    }

    private void CheckoutTree(Hash treeHash, string prefix = "")
    {
        Tree? tree = Objects.ReadTree(treeHash);
        if (tree == null) return;

        foreach (var entry in tree.Entries)
        {
            string path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            string fullPath = Path.Combine(RootPath, path);

            if (entry.Mode == FileMode.Directory)
            {
                PathUtil.EnsureDirectory(fullPath);
                CheckoutTree(entry.ObjectHash, path);
            }
            else
            {
                byte[]? content = Objects.ReadBlob(entry.ObjectHash);
                if (content != null)
                {
                    PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllBytes(fullPath, content);
                    Index.Add(path, entry.ObjectHash, entry.Mode);
                }
            }
        }
    }

    public void StageFile(string path)
    {
        string fullPath = Path.Combine(RootPath, path);
        if (!File.Exists(fullPath))
        {
            Index.Remove(path);
            return;
        }

        Hash hash = Objects.WriteBlobFromFile(fullPath);
        FileMode mode = FileMode.Normal;
        try
        {
            var fi = new FileInfo(fullPath);
            if ((fi.Attributes & FileAttributes.ReadOnly) == 0 && Helpers.IsWindows)
                mode = FileMode.Normal;
        }
        catch { }

        Index.Add(path, hash, mode);
    }

    public void UnstageFile(string path)
    {
        Index.Remove(path);
    }

    public LockFile AcquireLock(string lockName = "sm.lock")
    {
        string lockPath = Path.Combine(SmPath, lockName);
        var lf = new LockFile(lockPath);
        if (!lf.TryAcquire())
            throw new InvalidOperationException($"Unable to acquire lock '{lockName}'. Another SourceManager process may be running in this repository.");
        return lf;
    }
}