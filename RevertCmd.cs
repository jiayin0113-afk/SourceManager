using System.Text;

namespace SourceManager;

public static class RevertCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool noEdit = args.GetBoolOption("no-edit");
        bool noCommit = args.GetBoolOption("no-commit");
        bool mainline = args.GetBoolOption("mainline");
        int? mainlineNum = args.GetIntOption("mainline-number");
        bool continueRevert = args.GetBoolOption("continue");
        bool abort = args.GetBoolOption("abort");
        bool skip = args.GetBoolOption("skip");
        bool quit = args.GetBoolOption("quit");

        if (abort)
        {
            return AbortRevert(repo);
        }

        if (continueRevert)
        {
            return ContinueRevert(repo);
        }

        if (skip || quit)
        {
            Terminal.WriteError("fatal: --skip and --quit are for sequencer-based revert (not yet implemented)");
            return 1;
        }

        string? commitRef = args.GetPositional(0);
        if (commitRef == null)
        {
            Terminal.WriteError("error: commit required for revert");
            return 1;
        }

        try
        {
            Hash commitHash;
            if (commitRef == "HEAD")
            {
                Hash? head = repo.Refs.GetHeadCommit();
                if (!head.HasValue) throw new InvalidOperationException("HEAD does not exist");
                commitHash = head.Value;
            }
            else if (Hash.TryParse(commitRef) is Hash h)
            {
                commitHash = h;
            }
            else if (commitRef.Length >= 4)
            {
                commitHash = Helpers.ResolvePartialHash(repo, commitRef);
            }
            else
            {
                Hash? branch = repo.Refs.GetBranch(commitRef);
                if (!branch.HasValue) throw new InvalidOperationException($"Unknown revision: {commitRef}");
                commitHash = branch.Value;
            }

            return RevertCommit(repo, commitHash, noEdit, noCommit, mainlineNum);
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }
    }

    private static int RevertCommit(Repository repo, Hash commitHash, bool noEdit,
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
            Terminal.WriteError("fatal: cannot revert root commit");
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

        var diffs = repo.Diff.DiffCommits(commitHash, parentHash);
        try
        {
            ApplyPatch(repo, diffs);
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"error: could not revert {commitHash.Short}: {ex.Message}");
            return 1;
        }

        var status = repo.GetStatus();
        if (status.Conflicted.Count > 0)
        {
            Terminal.WriteError("error: could not apply revert... Resolve conflicts manually.");
            repo.Refs.SetRevertHead(commitHash);
            return 1;
        }

        if (!noCommit)
        {
            string msg = noEdit
                ? $"Revert \"{commit.Message.Split('\n')[0]}\"\n\nThis reverts commit {commitHash.ToHex()}."
                : $"Revert \"{commit.Message.Split('\n')[0]}\"";
            Hash treeHash = repo.WriteTreeFromIndex();
            var parents = new List<Hash> { headCommit.Value };

            Hash resultHash = repo.CreateCommit(treeHash, parents, msg);
            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch != null)
                repo.Refs.SetBranch(currentBranch, resultHash, $"revert: Revert \"{commit.Message.Split('\n')[0]}\"");
            else
                repo.Refs.SetHeadDetached(resultHash);
        }

        Terminal.WriteSuccess($"Reverted commit {commitHash.Short}");
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
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
                repo.Index.Remove(diff.NewPath);
            }
            else if (diff.Kind == ChangeKind.Deleted)
            {
                byte[]? content = repo.Objects.ReadBlob(diff.OldHash!.Value);
                if (content != null)
                {
                    File.WriteAllText(fullPath, Encoding.UTF8.GetString(content));
                    repo.StageFile(diff.NewPath);
                }
            }
            else
            {
                string? existingText = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                string patched = repo.Diff.ApplyPatch(existingText, diff.Hunks);
                File.WriteAllText(fullPath, patched);
                repo.StageFile(diff.NewPath);
            }
        }
    }

    private static int AbortRevert(Repository repo)
    {
        Hash? revertHead = repo.Refs.GetRevertHead();
        if (revertHead == null)
        {
            Terminal.WriteError("error: no revert in progress");
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
        repo.Refs.ClearRevertHead();
        Terminal.WriteSuccess("Revert aborted.");
        return 0;
    }

    private static int ContinueRevert(Repository repo)
    {
        Hash? revertHead = repo.Refs.GetRevertHead();
        if (revertHead == null)
        {
            Terminal.WriteError("error: no revert in progress");
            return 1;
        }

        Hash? headCommit = repo.Refs.GetHeadCommit();
        if (!headCommit.HasValue)
        {
            Terminal.WriteError("fatal: no HEAD commit");
            return 1;
        }

        Commit? revertCommit = repo.Objects.ReadCommit(revertHead.Value);
        string msg = revertCommit != null
            ? $"Revert \"{revertCommit.Message.Split('\n')[0]}\"\n\nThis reverts commit {revertHead.Value.ToHex()}."
            : $"Revert commit {revertHead.Value.Short}";

        Hash treeHash = repo.WriteTreeFromIndex();
        var parents = new List<Hash> { headCommit.Value };
        Hash resultHash = repo.CreateCommit(treeHash, parents, msg);

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (currentBranch != null)
            repo.Refs.SetBranch(currentBranch, resultHash, $"revert: continue");
        else
            repo.Refs.SetHeadDetached(resultHash);

        repo.Refs.ClearRevertHead();
        Terminal.WriteSuccess("Revert continued.");
        return 0;
    }
}