using System.Text;

namespace SourceManager;

public class RefStore
{
    private readonly string _refsPath;
    private readonly string _headsPath;
    private readonly string _tagsPath;
    private readonly string _remotesPath;
    private readonly string _headPath;
    private readonly string _reflogPath;
    private readonly string _stashPath;
    private readonly string _mergeHeadPath;
    private readonly string _cherryPickHeadPath;
    private readonly string _revertHeadPath;
    private readonly string _bisectPath;
    private readonly Repository _repo;

    public RefStore(Repository repo)
    {
        _repo = repo;
        _refsPath = Path.Combine(repo.SmPath, "refs");
        _headsPath = Path.Combine(_refsPath, "heads");
        _tagsPath = Path.Combine(_refsPath, "tags");
        _remotesPath = Path.Combine(_refsPath, "remotes");
        _headPath = Path.Combine(repo.SmPath, "HEAD");
        _reflogPath = Path.Combine(repo.SmPath, "logs");
        _stashPath = Path.Combine(repo.SmPath, "stash");
        _mergeHeadPath = Path.Combine(repo.SmPath, "MERGE_HEAD");
        _cherryPickHeadPath = Path.Combine(repo.SmPath, "CHERRY_PICK_HEAD");
        _revertHeadPath = Path.Combine(repo.SmPath, "REVERT_HEAD");
        _bisectPath = Path.Combine(repo.SmPath, "BISECT_LOG");
    }

    /// <summary>
    /// Creates the ref/reflog directory skeleton. This is deliberately NOT called from the
    /// constructor: constructing a Repository during a lookup (Repository.Find) must never
    /// create a .sm directory as a side effect. Call it on every write path instead.
    /// </summary>
    public void EnsureDirectories()
    {
        PathUtil.EnsureDirectory(_headsPath);
        PathUtil.EnsureDirectory(_tagsPath);
        PathUtil.EnsureDirectory(_remotesPath);
        PathUtil.EnsureDirectory(_reflogPath);
        PathUtil.EnsureDirectory(Path.Combine(_reflogPath, "refs", "heads"));
        PathUtil.EnsureDirectory(Path.Combine(_reflogPath, "refs", "remotes"));
        PathUtil.EnsureDirectory(Path.Combine(_reflogPath, "refs", "tags"));
    }

    public string? GetCurrentBranch()
    {
        if (!File.Exists(_headPath)) return null;
        string head = File.ReadAllText(_headPath).Trim();
        if (head.StartsWith("ref: refs/heads/"))
            return head[16..];
        return null;
    }

    /// <summary>
    /// Points HEAD at a branch (symbolic ref). Any move of HEAD is recorded in HEAD's reflog so
    /// that checkout/switch and the "previous branch" machinery have a history to consult.
    /// </summary>
    public void SetHeadBranch(string branchName, string? message = null)
    {
        Hash? oldHead = GetHeadCommit();
        string from = GetCurrentBranch() ?? (oldHead.HasValue ? ShortOf(oldHead.Value) : "HEAD");

        PathUtil.EnsureDirectory(Path.GetDirectoryName(_headPath)!);
        File.WriteAllText(_headPath, $"ref: refs/heads/{branchName}\n");

        Hash newHead = GetBranch(branchName) ?? Hash.Zero;
        Hash oldValue = oldHead ?? Hash.Zero;
        message ??= $"checkout: moving from {from} to {branchName}";

        // An unborn branch pointing at a zero hash is not a move; do not fabricate an entry.
        if (!oldValue.Equals(Hash.Zero) || !newHead.Equals(Hash.Zero))
            WriteReflog("HEAD", oldValue, newHead, message);
    }

    /// <summary>Detaches HEAD at a commit, recording the move in HEAD's reflog.</summary>
    public void SetHeadDetached(Hash commitHash, string? message = null)
    {
        Hash? oldHead = GetHeadCommit();
        string from = GetCurrentBranch() ?? (oldHead.HasValue ? ShortOf(oldHead.Value) : "HEAD");

        PathUtil.EnsureDirectory(Path.GetDirectoryName(_headPath)!);
        File.WriteAllText(_headPath, $"{commitHash.ToHex()}\n");

        message ??= $"checkout: moving from {from} to {ShortOf(commitHash)}";
        WriteReflog("HEAD", oldHead ?? Hash.Zero, commitHash, message);
    }

    private static string ShortOf(Hash hash) => hash.Equals(Hash.Zero) ? "HEAD" : hash.Short;

    public bool IsDetached()
    {
        if (!File.Exists(_headPath)) return true;
        string head = File.ReadAllText(_headPath).Trim();
        return !head.StartsWith("ref: ");
    }

    public Hash? GetHeadCommit()
    {
        if (!File.Exists(_headPath)) return null;
        string head = File.ReadAllText(_headPath).Trim();
        if (head.StartsWith("ref: refs/heads/"))
        {
            return GetBranch(head[16..]);
        }
        if (head.Length >= 64)
            return Hash.Parse(head[..64]);
        return null;
    }

    public Hash? GetBranch(string branchName)
    {
        string refPath = Path.Combine(_headsPath, branchName);
        if (!File.Exists(refPath)) return null;
        string content = File.ReadAllText(refPath).Trim();
        if (content.Length >= 64)
            return Hash.Parse(content[..64]);
        return null;
    }

    public void SetBranch(string branchName, Hash commitHash, string? message = null)
    {
        string refPath = Path.Combine(_headsPath, branchName);
        PathUtil.EnsureDirectory(_headsPath);

        Hash? oldHash = GetBranch(branchName);
        File.WriteAllText(refPath, $"{commitHash.ToHex()}\n");

        string msg = message ?? $"branch: update {branchName}";
        WriteReflog($"refs/heads/{branchName}", oldHash ?? Hash.Zero, commitHash, msg);

        // When the updated branch is the one HEAD is on, HEAD moves too — record it so
        // "HEAD@{n}" reflects commits, resets and merges made on the current branch.
        if (branchName == GetCurrentBranch())
            WriteReflog("HEAD", oldHash ?? Hash.Zero, commitHash, msg);
    }

    public void CreateBranch(string branchName, Hash commitHash)
    {
        string refPath = Path.Combine(_headsPath, branchName);
        PathUtil.EnsureDirectory(Path.GetDirectoryName(refPath)!);
        if (File.Exists(refPath))
            throw new InvalidOperationException($"Branch '{branchName}' already exists");
        File.WriteAllText(refPath, $"{commitHash.ToHex()}\n");
        WriteReflog($"refs/heads/{branchName}", Hash.Zero, commitHash, $"branch: Created from {commitHash.Short}");
    }

    public void DeleteBranch(string branchName)
    {
        string refPath = Path.Combine(_headsPath, branchName);
        if (!File.Exists(refPath))
            throw new InvalidOperationException($"Branch '{branchName}' does not exist");

        File.Delete(refPath);
        // A deleted branch takes its reflog with it, exactly as Git does: there is nothing left
        // for the log to describe. (The old code appended a bogus HEAD entry pointing at Zero,
        // which corrupted "HEAD@{0}".)
        DeleteReflog($"refs/heads/{branchName}");
    }

    public List<BranchInfo> ListBranches()
    {
        var branches = new List<BranchInfo>();
        string? currentBranch = GetCurrentBranch();

        // Recursive: "feature/x" is a legitimate branch name and must not disappear
        // just because its ref file lives in a subdirectory.
        foreach (string file in EnumerateRefFiles(_headsPath))
        {
            string name = Path.GetRelativePath(_headsPath, file).Replace('\\', '/');
            string content = File.ReadAllText(file).Trim();
            if (content.Length < 64) continue;

            string? upstream = GetBranchUpstream(name);
            string? remote = GetBranchRemote(name);

            branches.Add(new BranchInfo
            {
                Name = name,
                TipHash = Hash.Parse(content[..64]),
                UpstreamBranch = upstream,
                RemoteName = remote,
                IsHead = name == currentBranch
            });
        }
        return branches;
    }

    private static IEnumerable<string> EnumerateRefFiles(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (string sub in Directory.GetDirectories(dir))
            foreach (string f in EnumerateRefFiles(sub))
                yield return f;
        foreach (string f in Directory.GetFiles(dir))
            yield return f;
    }

    public Hash? GetTag(string tagName)
    {
        string tagPath = Path.Combine(_tagsPath, tagName);
        if (!File.Exists(tagPath)) return null;
        string content = File.ReadAllText(tagPath).Trim();
        if (content.Length >= 64)
            return Hash.Parse(content[..64]);
        return null;
    }

    public void SetTag(string tagName, Hash targetHash, string? message = null)
    {
        string tagPath = Path.Combine(_tagsPath, tagName);
        PathUtil.EnsureDirectory(Path.GetDirectoryName(tagPath)!);
        File.WriteAllText(tagPath, $"{targetHash.ToHex()}\n");
        WriteReflog($"refs/tags/{tagName}", Hash.Zero, targetHash, message ?? $"tag: {tagName}");
    }

    public void DeleteTag(string tagName)
    {
        string tagPath = Path.Combine(_tagsPath, tagName);
        if (File.Exists(tagPath))
            File.Delete(tagPath);
        DeleteReflog($"refs/tags/{tagName}");
    }

    public List<string> ListTags()
    {
        var tags = new List<string>();
        if (!Directory.Exists(_tagsPath)) return tags;
        foreach (string file in Directory.GetFiles(_tagsPath))
            tags.Add(Path.GetFileName(file));
        return tags;
    }

    public Hash? GetRemoteBranch(string remoteName, string branchName)
    {
        string refPath = Path.Combine(_remotesPath, remoteName, branchName);
        if (!File.Exists(refPath)) return null;
        string content = File.ReadAllText(refPath).Trim();
        if (content.Length >= 64)
            return Hash.Parse(content[..64]);
        return null;
    }

    public void SetRemoteBranch(string remoteName, string branchName, Hash commitHash)
    {
        string dir = Path.Combine(_remotesPath, remoteName);
        PathUtil.EnsureDirectory(dir);
        string refPath = Path.Combine(dir, branchName);
        Hash? oldHash = GetRemoteBranch(remoteName, branchName);
        File.WriteAllText(refPath, $"{commitHash.ToHex()}\n");
        WriteReflog($"refs/remotes/{remoteName}/{branchName}", oldHash ?? Hash.Zero, commitHash, $"fetch: {remoteName}/{branchName}");
    }

    public void DeleteRemoteBranch(string remoteName, string branchName)
    {
        string refPath = Path.Combine(_remotesPath, remoteName, branchName);
        if (File.Exists(refPath)) File.Delete(refPath);
        DeleteReflog($"refs/remotes/{remoteName}/{branchName}");
    }

    public List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null)
    {
        var result = new List<(string remote, string branch, Hash hash)>();
        if (!Directory.Exists(_remotesPath)) return result;

        foreach (string remoteDir in Directory.GetDirectories(_remotesPath))
        {
            string rn = Path.GetFileName(remoteDir);
            if (remoteName != null && rn != remoteName) continue;
            foreach (string file in Directory.GetFiles(remoteDir))
            {
                string bn = Path.GetFileName(file);
                string content = File.ReadAllText(file).Trim();
                if (content.Length >= 64)
                    result.Add((rn, bn, Hash.Parse(content[..64])));
            }
        }
        return result;
    }

    public string? GetBranchUpstream(string branchName)
    {
        string configKey = $"branch.{branchName}.remote";
        string? remote = _repo.Config.Get(configKey.Split('.')[0], configKey.Split('.')[1] + "." + configKey.Split('.')[2]);
        if (remote == null) return null;
        string mergeKey = $"branch.{branchName}.merge";
        string? merge = _repo.Config.Get(mergeKey.Split('.')[0], mergeKey.Split('.')[1] + "." + mergeKey.Split('.')[2]);
        if (merge == null) return null;
        return $"{remote}/{merge?.Replace("refs/heads/", "")}";
    }

    public string? GetBranchRemote(string branchName)
    {
        return _repo.Config.Get("branch", $"{branchName}.remote");
    }

    public void SetBranchUpstream(string branchName, string remote, string remoteBranch)
    {
        _repo.Config.Set("branch", $"{branchName}.remote", remote);
        _repo.Config.Set("branch", $"{branchName}.merge", $"refs/heads/{remoteBranch}");
    }

    public void SetMergeHead(Hash hash)
    {
        File.WriteAllText(_mergeHeadPath, $"{hash.ToHex()}\n");
    }

    public Hash? GetMergeHead()
    {
        if (!File.Exists(_mergeHeadPath)) return null;
        string content = File.ReadAllText(_mergeHeadPath).Trim();
        if (content.Length >= 64) return Hash.Parse(content[..64]);
        return null;
    }

    public void ClearMergeHead()
    {
        if (File.Exists(_mergeHeadPath)) File.Delete(_mergeHeadPath);
    }

    public void SetCherryPickHead(Hash hash)
    {
        File.WriteAllText(_cherryPickHeadPath, $"{hash.ToHex()}\n");
    }

    public Hash? GetCherryPickHead()
    {
        if (!File.Exists(_cherryPickHeadPath)) return null;
        string content = File.ReadAllText(_cherryPickHeadPath).Trim();
        if (content.Length >= 64) return Hash.Parse(content[..64]);
        return null;
    }

    public void ClearCherryPickHead()
    {
        if (File.Exists(_cherryPickHeadPath)) File.Delete(_cherryPickHeadPath);
    }

    public void SetRevertHead(Hash hash)
    {
        File.WriteAllText(_revertHeadPath, $"{hash.ToHex()}\n");
    }

    public Hash? GetRevertHead()
    {
        if (!File.Exists(_revertHeadPath)) return null;
        string content = File.ReadAllText(_revertHeadPath).Trim();
        if (content.Length >= 64) return Hash.Parse(content[..64]);
        return null;
    }

    public void ClearRevertHead()
    {
        if (File.Exists(_revertHeadPath)) File.Delete(_revertHeadPath);
    }

    /// <summary>
    /// Appends one reflog entry. The line format is
    /// "&lt;old&gt; &lt;new&gt; &lt;name&gt; &lt;email&gt; &lt;unix&gt; &lt;tz&gt;\t&lt;message&gt;": the timestamp lives
    /// inside the signature, so it round-trips through <see cref="Signature.Parse"/>.
    /// </summary>
    public void WriteReflog(string refName, Hash oldHash, Hash newHash, string message)
    {
        string logPath = ReflogFilePath(refName);
        PathUtil.EnsureDirectory(Path.GetDirectoryName(logPath)!);

        var entry = new ReflogEntry
        {
            OldHash = oldHash,
            NewHash = newHash,
            Author = Signature.Now(_repo.Config.GetUserName(), _repo.Config.GetUserEmail()),
            Message = message,
            Timestamp = DateTimeOffset.Now
        };

        File.AppendAllText(logPath, FormatReflogLine(entry));
    }

    /// <summary>Newest-first view of a ref's reflog; <paramref name="maxCount"/> 0 means "all".</summary>
    public List<ReflogEntry> GetReflog(string refName = "HEAD", int maxCount = 100)
    {
        var entries = ReadReflogOldestFirst(refName);
        if (maxCount > 0 && entries.Count > maxCount)
            entries.RemoveRange(0, entries.Count - maxCount);
        entries.Reverse();
        return entries;
    }

    /// <summary>Reads a ref's reflog in file (oldest-first) order, preserving every field.</summary>
    public List<ReflogEntry> ReadReflogOldestFirst(string refName)
    {
        var entries = new List<ReflogEntry>();
        string logPath = ReflogFilePath(refName);
        if (!File.Exists(logPath)) return entries;

        foreach (string line in File.ReadAllLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ReflogEntry? entry = ParseReflogLine(line);
            if (entry != null) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Replaces a ref's reflog with the supplied entries (oldest-first).</summary>
    public void RewriteReflog(string refName, IEnumerable<ReflogEntry> entries)
    {
        string logPath = ReflogFilePath(refName);
        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.Append(FormatReflogLine(entry));
        PathUtil.EnsureDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, sb.ToString());
    }

    public void DeleteReflog(string refName)
    {
        string logPath = ReflogFilePath(refName);
        if (File.Exists(logPath)) File.Delete(logPath);
    }

    /// <summary>Writes a ref without touching its reflog (used when rewriting history metadata).</summary>
    public void WriteRefNoLog(string refName, Hash hash)
    {
        string fullPath = ResolveRefFilePath(refName);
        PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"{hash.ToHex()}\n");
    }

    private static string FormatReflogLine(ReflogEntry entry)
    {
        // A reflog is one line per entry; any control character in the message would split or
        // tab-delimit it, so it is flattened before writing.
        string message = entry.Message.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        return $"{entry.OldHash.ToHex()} {entry.NewHash.ToHex()} {entry.Author.Format()}\t{message}\n";
    }

    private static ReflogEntry? ParseReflogLine(string line)
    {
        int tab = line.IndexOf('\t');
        string meta = tab >= 0 ? line[..tab] : line;
        string message = tab >= 0 ? line[(tab + 1)..] : string.Empty;

        int firstSpace = meta.IndexOf(' ');
        if (firstSpace <= 0) return null;
        int secondSpace = meta.IndexOf(' ', firstSpace + 1);
        if (secondSpace < 0) return null;

        Hash? oldHash = Hash.TryParse(meta[..firstSpace]);
        Hash? newHash = Hash.TryParse(meta[(firstSpace + 1)..secondSpace]);
        if (oldHash == null || newHash == null) return null;

        Signature author;
        try { author = Signature.Parse(meta[(secondSpace + 1)..]); }
        catch { author = Signature.Now("unknown", "unknown"); }

        return new ReflogEntry
        {
            OldHash = oldHash.Value,
            NewHash = newHash.Value,
            Author = author,
            Message = message,
            Timestamp = author.When
        };
    }

    private string ReflogFilePath(string refName)
        => refName == "HEAD"
            ? Path.Combine(_reflogPath, "HEAD")
            : Path.Combine(_reflogPath, refName.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Maps a full ref name to the file that stores it, accepting "refs/..." or a bare name.</summary>
    private string ResolveRefFilePath(string refName)
    {
        string[] parts = refName.Split('/');
        if (parts.Length > 0 && parts[0] == "refs")
        {
            if (parts.Length > 1 && parts[1] == "heads")
                return Path.Combine(_headsPath, string.Join('/', parts[2..]));
            if (parts.Length > 1 && parts[1] == "tags")
                return Path.Combine(_tagsPath, string.Join('/', parts[2..]));
            if (parts.Length > 1 && parts[1] == "remotes")
                return Path.Combine(_remotesPath, string.Join('/', parts[2..]));
            return Path.Combine(_refsPath, string.Join('/', parts[1..]));
        }
        return Path.Combine(_refsPath, refName);
    }

    public List<StashEntry> GetStashes()
    {
        var stashes = new List<StashEntry>();
        string stashListPath = Path.Combine(_repo.SmPath, "stash_list");
        if (!File.Exists(stashListPath)) return stashes;

        string[] lines = File.ReadAllLines(stashListPath);
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var entry = new StashEntry
            {
                CommitHash = Hash.Parse(parts[0]),
                Message = parts.Length > 1 ? parts[1] : "WIP",
                Timestamp = DateTimeOffset.Now
            };
            stashes.Add(entry);
        }
        return stashes;
    }

    public void AddStash(StashEntry entry)
    {
        string stashListPath = Path.Combine(_repo.SmPath, "stash_list");
        string line = $"{entry.CommitHash.ToHex()}\t{entry.Message}\n";
        string content = line;
        if (File.Exists(stashListPath))
            content = line + File.ReadAllText(stashListPath);
        File.WriteAllText(stashListPath, content);
    }

    public void RemoveStash(int index)
    {
        string stashListPath = Path.Combine(_repo.SmPath, "stash_list");
        if (!File.Exists(stashListPath)) return;

        List<string> lines = new(File.ReadAllLines(stashListPath));
        if (index >= 0 && index < lines.Count)
            lines.RemoveAt(index);
        File.WriteAllLines(stashListPath, lines);

        if (lines.Count == 0)
            File.Delete(stashListPath);
    }

    public void PopStash(int index)
    {
        string stashListPath = Path.Combine(_repo.SmPath, "stash_list");
        if (!File.Exists(stashListPath)) return;

        List<string> lines = new(File.ReadAllLines(stashListPath));
        if (index >= 0 && index < lines.Count)
            lines.RemoveAt(index);
        File.WriteAllLines(stashListPath, lines);

        if (lines.Count == 0)
            File.Delete(stashListPath);
    }

    public void SetBisectLog(string entry)
    {
        File.AppendAllText(_bisectPath, entry + "\n");
    }

    public string[] GetBisectLog()
    {
        if (!File.Exists(_bisectPath)) return Array.Empty<string>();
        return File.ReadAllLines(_bisectPath);
    }

    public void ResetBisect()
    {
        if (File.Exists(_bisectPath))
            File.Delete(_bisectPath);
    }

    /// <summary>
    /// Enumerates every ref below refs/. Enumeration is RECURSIVE: a ref name may contain
    /// slashes (refs/heads/feature/x), and a non-recursive scan made those branches invisible
    /// to callers — which in turn made reachability analysis treat their history as garbage.
    /// </summary>
    public IEnumerable<string> GetAllRefNames()
    {
        foreach (string refName in EnumerateRefNames(_refsPath, "refs"))
            yield return refName;
    }

    private static IEnumerable<string> EnumerateRefNames(string dir, string prefix)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (string sub in Directory.GetDirectories(dir))
            foreach (string nested in EnumerateRefNames(sub, prefix + "/" + Path.GetFileName(sub)))
                yield return nested;
        foreach (string file in Directory.GetFiles(dir))
            yield return prefix + "/" + Path.GetFileName(file);
    }

    /// <summary>Resolves a full ref name ("refs/heads/main") to its target hash.</summary>
    public Hash? ResolveRefName(string refName)
    {
        if (string.IsNullOrEmpty(refName)) return null;
        if (refName == "HEAD") return GetHeadCommit();

        string relative = refName.StartsWith("refs/", StringComparison.Ordinal) ? refName[5..] : refName;
        string path = Path.Combine(_refsPath, relative.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path)) return null;
        string content = File.ReadAllText(path).Trim();

        // A symbolic ref inside refs/ points elsewhere.
        if (content.StartsWith("ref: ", StringComparison.Ordinal))
            return ResolveRefName(content[5..].Trim());

        if (content.Length >= 64) return Hash.Parse(content[..64]);
        return null;
    }

    // ─── Rebase state ────────────────────────────────────────────────────────────
    // Stored as plain files under .sm so an interrupted rebase can be resumed by a later
    // process. (The previous implementation wrote these under refs/ but read them from
    // refs/heads/, so --continue and --skip could never find the state.)

    private string RebaseTodoPath => Path.Combine(_repo.SmPath, "REBASE_TODO");
    private string RebaseIndexPath => Path.Combine(_repo.SmPath, "REBASE_INDEX");

    public bool RebaseInProgress => File.Exists(RebaseTodoPath) || RebasePlanExists();

    private bool RebasePlanExists() => File.Exists(Path.Combine(_repo.SmPath, "REBASE_PLAN"));

    public void WriteRebaseState(IEnumerable<Hash> remaining)
    {
        File.WriteAllLines(RebaseTodoPath, remaining.Select(h => h.ToHex()));
        WriteRebaseIndex(0);
    }

    public void WriteRebaseIndex(int index)
        => File.WriteAllText(RebaseIndexPath, index.ToString());

    public List<Hash> GetRebaseTodo()
    {
        var result = new List<Hash>();
        if (!File.Exists(RebaseTodoPath)) return result;
        foreach (string line in File.ReadAllLines(RebaseTodoPath))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 64 && Hash.TryParse(trimmed) is Hash h)
                result.Add(h);
        }
        return result;
    }

    public int GetRebaseIndex()
    {
        if (!File.Exists(RebaseIndexPath)) return 0;
        return int.TryParse(File.ReadAllText(RebaseIndexPath).Trim(), out int i) ? i : 0;
    }

    public void ClearRebaseState()
    {
        if (File.Exists(RebaseTodoPath)) File.Delete(RebaseTodoPath);
        if (File.Exists(RebaseIndexPath)) File.Delete(RebaseIndexPath);
    }

    public void WriteRef(string refName, Hash hash)
    {
        string fullName = refName.StartsWith("refs/", StringComparison.Ordinal) ? refName : "refs/" + refName;
        Hash? old = ResolveRefName(refName);
        WriteRefNoLog(fullName, hash);
        WriteReflog(fullName, old ?? Hash.Zero, hash, $"update-ref: {fullName}");
    }
}