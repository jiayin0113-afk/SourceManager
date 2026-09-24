namespace SourceManager;

public static class StashCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool list = args.GetBoolOption("list");
        bool show = args.GetBoolOption("show");
        bool pop = args.GetBoolOption("pop");
        bool apply = args.GetBoolOption("apply");
        bool drop = args.GetBoolOption("drop");
        string? branch = args.GetOption("branch");
        bool push = args.GetBoolOption("push");
        bool keepIndex = args.GetBoolOption("keep-index");
        bool includeUntracked = args.GetBoolOption("include-untracked");
        bool all = args.GetBoolOption("all");
        bool quiet = args.GetBoolOption("quiet");
        string? message = args.GetOption("message");

        // Accept the ordinary verb form (`sm stash list`, `sm stash pop`). These were only
        // reachable as --list/--pop, so `sm stash list` fell through to PushStash and silently
        // CREATED a stash instead of listing them.
        string? verb = null;
        var rest = new List<string>(args.PositionalArgs);
        if (!list && !show && !pop && !apply && !drop && !push && branch == null && rest.Count > 0)
        {
            verb = rest[0];
            rest.RemoveAt(0);
            switch (verb)
            {
                case "list": list = true; break;
                case "show": show = true; break;
                case "pop": pop = true; break;
                case "apply": apply = true; break;
                case "drop": drop = true; break;
                case "push":
                case "save": push = true; break;
                case "branch":
                    if (rest.Count == 0)
                    {
                        Terminal.WriteError("error: usage: sm stash branch <branch> [<stash>]");
                        return 1;
                    }
                    branch = rest[0];
                    rest.RemoveAt(0);
                    break;
                case "clear":
                    return ClearStashes(repo);
                default:
                    Terminal.WriteError($"error: unknown stash subcommand '{verb}'");
                    Terminal.WriteError("usage: sm stash [list|show|pop|apply|drop|branch|clear] [<stash>]");
                    return 1;
            }
        }
        else if (!list && !show && !pop && !apply && !drop && !push && branch == null)
        {
            push = true;
        }

        string? stashRef = rest.Count > 0 ? rest[0] : null;

        if (list)
        {
            return ListStashes(repo);
        }

        if (show)
        {
            return ShowStash(repo, stashRef);
        }

        if (drop)
        {
            return DropStash(repo, stashRef);
        }

        if (pop)
        {
            return PopStash(repo, stashRef, keepIndex);
        }

        if (apply)
        {
            return ApplyStash(repo, stashRef, keepIndex);
        }

        if (branch != null)
        {
            return BranchFromStash(repo, branch, stashRef);
        }

        if (push)
        {
            return PushStash(repo, message, keepIndex, includeUntracked, all, quiet);
        }

        return 0;
    }

    private static int ClearStashes(Repository repo)
    {
        var stashes = repo.Refs.GetStashes();
        for (int i = stashes.Count - 1; i >= 0; i--)
            repo.Refs.RemoveStash(i);
        Terminal.WriteSuccess($"Cleared {stashes.Count} stash entr{(stashes.Count == 1 ? "y" : "ies")}.");
        return 0;
    }

    private static int PushStash(Repository repo, string? message, bool keepIndex,
        bool includeUntracked, bool all, bool quiet)
    {
        var status = repo.GetStatus();

        // A staged-only change lives in Staged (index vs HEAD), not in Changes (worktree vs
        // index). Looking only at Changes made `sm stash` report "No local changes to save"
        // while a staged edit was sitting right there.
        bool hasStaged = status.Staged.Count > 0;
        bool hasUnstaged = status.Changes.Count > 0;
        bool hasUntracked = status.Untracked.Count > 0 && (includeUntracked || all);
        bool hasChanges = hasStaged || hasUnstaged || hasUntracked;

        if (!hasChanges)
        {
            Terminal.WriteLine("No local changes to save");
            return 0;
        }

        // The stash records the working tree AND, when there is staged work, the index tree,
        // so applying it can restore the staged/unstaged split.
        Hash? headCommit = repo.Refs.GetHeadCommit();
        string stashMsg = message ?? $"WIP on {repo.Refs.GetCurrentBranch() ?? "(detached)"}: {headCommit?.Short ?? "0000000"}";

        Hash? indexTreeHash = null;
        if (hasStaged)
            indexTreeHash = repo.WriteTreeFromIndex();

        // Capture the working tree as it stands, THEN fold it into the stash tree.
        var untrackedToStage = new List<string>();
        if (hasUntracked)
            untrackedToStage.AddRange(status.Untracked);

        foreach (var (path, kind) in status.Changes)
        {
            if (kind == ChangeKind.Deleted)
            {
                repo.Index.Remove(path);
            }
        }

        // Stage the current working tree so the stash commit captures unstaged edits too.
        foreach (var (path, kind) in status.Changes)
        {
            if (kind != ChangeKind.Deleted)
                repo.StageFile(path);
        }
        foreach (string path in untrackedToStage)
            repo.StageFile(path);

        Hash stashTreeHash = repo.WriteTreeFromIndex();
        var parents = new List<Hash>();
        if (headCommit.HasValue) parents.Add(headCommit.Value);

        Hash stashCommitHash = repo.CreateCommit(stashTreeHash, parents, stashMsg);

        var stashEntry = new StashEntry
        {
            CommitHash = stashCommitHash,
            IndexHash = indexTreeHash,
            UntrackedHash = null,
            Message = stashMsg,
            Author = Signature.Now(repo.Config.GetUserName(), repo.Config.GetUserEmail()),
            Timestamp = DateTimeOffset.Now
        };

        repo.Refs.AddStash(stashEntry);

        if (headCommit.HasValue)
        {
            repo.CheckoutCommit(headCommit.Value);
        }

        // Untracked files that were stashed must leave the working tree.
        foreach (string path in untrackedToStage)
        {
            string full = Path.Combine(repo.RootPath, path);
            if (File.Exists(full))
            {
                try { File.Delete(full); } catch { }
            }
        }

        if (!keepIndex)
        {
            repo.Index.Clear();
            repo.Index.Save();
        }
        else if (indexTreeHash.HasValue)
        {
            // Restore the index to what was staged before the stash.
            RestoreIndexFromTree(repo, indexTreeHash.Value);
        }

        if (!quiet)
        {
            string shortHash = stashCommitHash.Short;
            Terminal.WriteSuccess($"Saved working directory and index state WIP: {stashMsg}");
        }

        return 0;
    }

    /// <summary>
    /// Rebuilds the index from a tree, used by --keep-index to put back exactly what was staged
    /// before the stash was taken.
    /// </summary>
    private static void RestoreIndexFromTree(Repository repo, Hash treeHash)
    {
        repo.Index.Clear();
        var stack = new Stack<(Hash Tree, string Prefix)>();
        stack.Push((treeHash, ""));
        while (stack.Count > 0)
        {
            var (hash, prefix) = stack.Pop();
            Tree? tree = repo.Objects.ReadTree(hash);
            if (tree == null) continue;

            foreach (var entry in tree.Entries)
            {
                string path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
                if (entry.Mode == FileMode.Directory)
                    stack.Push((entry.ObjectHash, path));
                else
                    repo.Index.Add(path, entry.ObjectHash, entry.Mode);
            }
        }
        repo.Index.Save();
    }

    private static int ListStashes(Repository repo)
    {
        var stashes = repo.Refs.GetStashes();
        if (stashes.Count == 0)
        {
            return 0;
        }

        for (int i = 0; i < stashes.Count; i++)
        {
            var stash = stashes[i];
            Terminal.Write($"stash@{{{i}}}: ", Terminal.Color.Yellow);
            Console.WriteLine($"{stash.Message}");
        }

        return 0;
    }

    private static int ShowStash(Repository repo, string? stashRef)
    {
        var stashes = repo.Refs.GetStashes();
        int index = ParseStashIndex(stashRef, stashes.Count);
        if (index < 0 || index >= stashes.Count)
        {
            Terminal.WriteError($"error: stash@{stashRef ?? "0"} not found");
            return 1;
        }

        var stash = stashes[index];
        Commit? stashCommit = repo.Objects.ReadCommit(stash.CommitHash);
        if (stashCommit == null)
        {
            Terminal.WriteError("error: cannot read stash commit");
            return 1;
        }

        Console.WriteLine($"stash@{{{index}}}: {stash.Message}");
        Console.WriteLine();

        if (stashCommit.ParentHashes.Count > 0)
        {
            var diffs = repo.Diff.DiffCommits(stashCommit.ParentHashes[0], stash.CommitHash);
            foreach (var diff in diffs)
            {
                Console.WriteLine($"diff --git a/{diff.OldPath} b/{diff.NewPath}");
                foreach (var hunk in diff.Hunks)
                {
                    Console.WriteLine(hunk.Header);
                    foreach (var line in hunk.Lines)
                    {
                        char prefix = line.Type switch
                        {
                            DiffLineType.Added => '+',
                            DiffLineType.Deleted => '-',
                            _ => ' '
                        };
                        Terminal.Color color = line.Type switch
                        {
                            DiffLineType.Added => Terminal.Color.Green,
                            DiffLineType.Deleted => Terminal.Color.Red,
                            _ => Terminal.Color.Default
                        };
                        Terminal.WriteLine($"{prefix}{line.Text}", color);
                    }
                }
            }
        }
        else
        {
            if (stashCommit.TreeHash.HasValue)
            {
                var entries = repo.GetTreeEntries(stashCommit.TreeHash.Value);
                foreach (var (path, _) in entries)
                {
                    Console.WriteLine($"new file: {path}");
                }
            }
        }

        return 0;
    }

    private static int ApplyStash(Repository repo, string? stashRef, bool keepIndex)
    {
        var stashes = repo.Refs.GetStashes();
        int index = ParseStashIndex(stashRef, stashes.Count);
        if (index < 0 || index >= stashes.Count)
        {
            Terminal.WriteError($"error: stash@{stashRef ?? "0"} not found");
            return 1;
        }

        var stash = stashes[index];
        Commit? stashCommit = repo.Objects.ReadCommit(stash.CommitHash);
        if (stashCommit == null)
        {
            Terminal.WriteError("error: cannot read stash commit");
            return 1;
        }

        if (stashCommit.TreeHash.HasValue)
        {
            repo.CheckoutCommit(stash.CommitHash);
        }

        Terminal.WriteSuccess($"Applied stash@{{{index}}}");

        return 0;
    }

    private static int PopStash(Repository repo, string? stashRef, bool keepIndex)
    {
        var stashes = repo.Refs.GetStashes();
        int index = ParseStashIndex(stashRef, stashes.Count);
        if (index < 0 || index >= stashes.Count)
        {
            Terminal.WriteError($"error: stash@{stashRef ?? "0"} not found");
            return 1;
        }

        int result = ApplyStash(repo, stashRef, keepIndex);
        if (result != 0) return result;

        return DropStash(repo, index.ToString());
    }

    private static int DropStash(Repository repo, string? stashRef)
    {
        var stashes = repo.Refs.GetStashes();
        int index = ParseStashIndex(stashRef, stashes.Count);
        if (index < 0 || index >= stashes.Count)
        {
            Terminal.WriteError($"error: stash@{stashRef ?? "0"} not found");
            return 1;
        }

        var stash = stashes[index];
        repo.Refs.RemoveStash(index);
        Terminal.WriteSuccess($"Dropped stash@{{{index}}} ({stash.CommitHash.Short})");
        return 0;
    }

    private static int BranchFromStash(Repository repo, string branchName, string? stashRef)
    {
        var stashes = repo.Refs.GetStashes();
        int index = ParseStashIndex(stashRef, stashes.Count);
        if (index < 0 || index >= stashes.Count)
        {
            Terminal.WriteError($"error: stash@{stashRef ?? "0"} not found");
            return 1;
        }

        var stash = stashes[index];

        try
        {
            repo.Refs.CreateBranch(branchName, stash.CommitHash);
            Terminal.WriteSuccess($"Created branch '{branchName}' from stash@{{{index}}}");
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"error: {ex.Message}");
            return 1;
        }

        repo.Refs.RemoveStash(index);
        Terminal.WriteSuccess($"Dropped stash@{{{index}}}");
        return 0;
    }

    private static int ParseStashIndex(string? stashRef, int stashCount)
    {
        if (stashRef == null) return 0;
        if (stashRef.StartsWith("stash@{") && stashRef.EndsWith("}"))
        {
            string numStr = stashRef[7..^1];
            if (int.TryParse(numStr, out int idx)) return idx;
        }
        if (int.TryParse(stashRef, out int n)) return n;
        return 0;
    }
}