using System.Text;

namespace SourceManager;

public static class CherryPickCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool noCommit = args.GetBoolOption("no-commit");
        string? strategy = args.GetOption("strategy");
        bool continueCherryPick = args.GetBoolOption("continue");
        bool abort = args.GetBoolOption("abort");
        bool skip = args.GetBoolOption("skip");
        bool quit = args.GetBoolOption("quit");
        int? mainline = args.GetIntOption("mainline");
        string? xEquiv = args.GetOption("x");

        if (abort)
            return AbortCherryPick(repo);

        if (continueCherryPick)
            return ContinueCherryPick(repo);

        if (skip || quit)
        {
            Terminal.WriteError("fatal: --skip and --quit are for sequencer-based cherry-pick (not yet implemented)");
            return 1;
        }

        var positional = args.PositionalArgs;
        if (positional.Count == 0)
        {
            Terminal.WriteError("error: commit required");
            return 1;
        }

        Hash? firstHash = null;
        List<string> failed = new();
        bool anySuccess = false;

        foreach (string commitRef in positional)
        {
            try
            {
                Hash commitHash = ResolveHash(repo, commitRef);
                int result = CherryPickSingle(repo, commitHash, noCommit, mainline);
                if (result != 0)
                {
                    failed.Add(commitRef);
                }
                else
                {
                    anySuccess = true;
                    if (firstHash == null) firstHash = commitHash;
                }
            }
            catch (Exception ex)
            {
                Terminal.WriteError($"error: {ex.Message}");
                return 1;
            }
        }

        if (failed.Count > 0)
        {
            Terminal.WriteError($"error: could not apply {string.Join(", ", failed)}");
            return 1;
        }

        return 0;
    }

    private static int CherryPickSingle(Repository repo, Hash commitHash,
        bool noCommit, int? mainline)
    {
        Commit? commit = repo.Objects.ReadCommit(commitHash);
        if (commit == null)
        {
            Terminal.WriteError($"fatal: bad object {commitHash.Short}");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        Hash parentHash;
        if (commit.ParentHashes.Count == 0)
        {
            Terminal.WriteError($"fatal: commit {commitHash.Short} is a root commit, cannot cherry-pick");
            return 1;
        }
        else if (commit.ParentHashes.Count == 1)
        {
            parentHash = commit.ParentHashes[0];
        }
        else
        {
            if (mainline.HasValue && mainline.Value > 0 && mainline.Value <= commit.ParentHashes.Count)
            {
                parentHash = commit.ParentHashes[mainline.Value - 1];
            }
            else
            {
                parentHash = commit.ParentHashes[0];
            }
        }

        var diffs = repo.Diff.DiffCommits(parentHash, commitHash);
        try
        {
            ApplyPatch(repo, diffs);
        }
        catch
        {
            Terminal.WriteError($"error: could not apply {commitHash.Short}");
            repo.Refs.SetCherryPickHead(commitHash);
            return 1;
        }

        var status = repo.GetStatus();
        if (status.Conflicted.Count > 0)
        {
            Terminal.WriteError("error: could not apply cherry-pick... Resolve conflicts manually.");
            repo.Refs.SetCherryPickHead(commitHash);
            return 1;
        }

        HashSet<string> modifiedPaths = new();
        foreach (var diff in diffs)
            modifiedPaths.Add(diff.NewPath);
        foreach (string path in modifiedPaths)
            repo.StageFile(path);

        if (!noCommit)
        {
            string msg = commit.Message;
            if (repo.Refs.GetCherryPickHead() != null)
            {
                msg += "\n(cherry picked from commit " + commitHash.ToHex() + ")";
            }

            Hash treeHash = repo.WriteTreeFromIndex();
            Hash resultHash = repo.CreateCommit(treeHash, new List<Hash> { headCommit.Value }, msg);

            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch != null)
                repo.Refs.SetBranch(currentBranch, resultHash, $"cherry-pick: {commitHash.Short} {commit.Message.Split('\n')[0]}");
            else
                repo.Refs.SetHeadDetached(resultHash);

            Terminal.WriteSuccess($"[{currentBranch ?? "detached HEAD"} {resultHash.Short}] {commit.Message.Split('\n')[0]}");
        }

        return 0;
    }

    private static void ApplyPatch(Repository repo, List<FileDiff> diffs)
    {
        foreach (var diff in diffs)
        {
            string fullPath = Path.Combine(repo.RootPath, diff.NewPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            if (diff.Kind == ChangeKind.Added)
            {
                byte[]? content = repo.Objects.ReadBlob(diff.NewHash!.Value);
                if (content != null)
                {
                    File.WriteAllText(fullPath, Encoding.UTF8.GetString(content));
                }
            }
            else if (diff.Kind == ChangeKind.Deleted)
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            else
            {
                string? existingText = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                string patched = repo.Diff.ApplyPatch(existingText, diff.Hunks);
                File.WriteAllText(fullPath, patched);
            }
        }
    }

    private static int AbortCherryPick(Repository repo)
    {
        Hash? cherryHead = repo.Refs.GetCherryPickHead();
        if (cherryHead == null)
        {
            Terminal.WriteError("error: no cherry-pick in progress");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        repo.CheckoutCommit(headCommit.Value);
        repo.Index.Clear();
        repo.Index.Save();
        repo.Refs.ClearCherryPickHead();
        Terminal.WriteSuccess("Cherry-pick aborted.");
        return 0;
    }

    private static int ContinueCherryPick(Repository repo)
    {
        Hash? cherryHead = repo.Refs.GetCherryPickHead();
        if (cherryHead == null)
        {
            Terminal.WriteError("error: no cherry-pick in progress");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        Commit? cherryCommit = repo.Objects.ReadCommit(cherryHead.Value);
        string msg = cherryCommit?.Message ?? "Cherry-pick";
        msg += "\n(cherry picked from commit " + cherryHead.Value.ToHex() + ")";

        Hash treeHash = repo.WriteTreeFromIndex();
        Hash resultHash = repo.CreateCommit(treeHash, new List<Hash> { headCommit.Value }, msg);

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (currentBranch != null)
            repo.Refs.SetBranch(currentBranch, resultHash, $"cherry-pick: continue");
        else
            repo.Refs.SetHeadDetached(resultHash);

        repo.Refs.ClearCherryPickHead();
        Terminal.WriteSuccess("Cherry-pick continued.");
        return 0;
    }

    private static Hash ResolveHash(Repository repo, string input)
    {
        if (input == "HEAD")
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue) throw new InvalidOperationException("HEAD does not exist");
            return head.Value;
        }
        if (Hash.TryParse(input) is Hash h) return h;
        if (input.Length >= 4)
        {
            try { return Helpers.ResolvePartialHash(repo, input); }
            catch { }
        }
        Hash? branch = repo.Refs.GetBranch(input);
        if (branch.HasValue) return branch.Value;
        Hash? tag = repo.Refs.GetTag(input);
        if (tag.HasValue) return tag.Value;
        throw new InvalidOperationException($"Unknown revision: {input}");
    }
}