namespace SourceManager;

public static class CommitCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        string? message = args.GetOption("message");
        bool all = args.GetBoolOption("all");
        bool amend = args.GetBoolOption("amend");
        bool allowEmpty = args.GetBoolOption("allow-empty");
        bool allowEmptyMessage = args.GetBoolOption("allow-empty-message");
        bool signoff = args.GetBoolOption("signoff");
        bool quiet = args.GetBoolOption("quiet");
        bool noVerify = args.GetBoolOption("no-verify");

        var paths = args.PositionalArgs;

        if (amend && !repo.Refs.GetHeadCommit().HasValue)
        {
            Terminal.WriteError("fatal: you have nothing to amend");
            return 1;
        }

        // Refuse to commit a merge that still has unresolved conflicts.
        var unresolved = ConflictState.GetUnresolved(repo);
        if (unresolved.Count > 0)
        {
            Terminal.WriteError($"error: Committing is not possible because you have unmerged files.");
            foreach (string path in unresolved)
                Terminal.WriteError($"    {path}");
            Terminal.WriteError("hint: Fix them up in the work tree, then use 'sm add <file>' to mark resolution.");
            return 1;
        }

        if (message == null && !amend)
        {
            message = ReadCommitMessage();
            if (string.IsNullOrWhiteSpace(message) && !allowEmptyMessage)
            {
                Terminal.WriteError("Aborting commit due to empty commit message.");
                return 1;
            }
        }

        // --all stages tracked modifications; it must be persisted to the on-disk index,
        // otherwise the next status/commit sees a stale index and re-reports the same edits.
        if (all)
        {
            var status = repo.GetStatus();
            foreach (var (path, kind) in status.Changes)
            {
                if (kind == ChangeKind.Deleted)
                    repo.Index.Remove(path);
                else
                    repo.StageFile(path);
            }
        }

        // Explicit paths are staged whether or not --only was passed; paths that are not
        // staged are simply not part of this commit.
        foreach (string path in paths)
            repo.StageFile(path);

        repo.Index.Save();

        // pre-commit runs against the staged index; a non-zero exit aborts before any object
        // is written, which is what makes it a useful guard.
        if (!noVerify && !Hooks.Run(repo, "pre-commit", abortOnFailure: true))
            return 1;

        Hash treeHash = repo.WriteTreeFromIndex();

        var parents = new List<Hash>();
        Hash? headCommit = repo.Refs.GetHeadCommit();
        Hash? mergeHead = repo.Refs.GetMergeHead();

        if (amend && headCommit.HasValue)
        {
            Commit? prevCommit = repo.Objects.ReadCommit(headCommit.Value);
            if (prevCommit != null)
            {
                parents.AddRange(prevCommit.ParentHashes);
                if (string.IsNullOrEmpty(message))
                    message = prevCommit.Message;
            }
        }
        else
        {
            if (headCommit.HasValue)
                parents.Add(headCommit.Value);

            // A merge in progress contributes the second parent; without this a commit made
            // after `merge --no-commit` silently recorded a single-parent history.
            if (mergeHead.HasValue)
                parents.Add(mergeHead.Value);
        }

        // Emptiness is "the tree equals the parent's tree", not "the index is non-empty".
        if (!allowEmpty && !amend && !mergeHead.HasValue)
        {
            bool treeUnchanged = false;
            if (headCommit.HasValue)
            {
                Commit? prev = repo.Objects.ReadCommit(headCommit.Value);
                treeUnchanged = prev?.TreeHash.HasValue == true && prev.TreeHash.Value.Equals(treeHash);
            }
            else
            {
                treeUnchanged = repo.Index.GetTrackedFiles().Count == 0;
            }

            if (treeUnchanged)
            {
                var st = repo.GetStatus();
                if (st.Changes.Count > 0 || st.Untracked.Count > 0)
                    Terminal.WriteError("no changes added to commit (use \"sm add\" and/or \"sm commit -a\")");
                else
                    Terminal.WriteError("nothing to commit, working tree clean");
                return 1;
            }
        }

        if (signoff && !string.IsNullOrEmpty(message))
        {
            string name = repo.Config.GetUserName();
            string email = repo.Config.GetUserEmail();
            string trailer = $"Signed-off-by: {name} <{email}>";
            if (!message.Contains(trailer, StringComparison.Ordinal))
                message += $"\n\n{trailer}";
        }

        if (message == null) message = "";

        // commit-msg receives a file holding the message and may rewrite it in place.
        if (!noVerify)
        {
            string msgFile = Path.Combine(Path.GetTempPath(), $"sm_commit_msg_{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(msgFile, message);
                if (!Hooks.Run(repo, "commit-msg", new[] { msgFile }, abortOnFailure: true))
                    return 1;
                message = File.ReadAllText(msgFile).TrimEnd('\n');
            }
            finally
            {
                try { File.Delete(msgFile); } catch { }
            }
        }

        Hash commitHash = repo.CreateCommit(treeHash, parents, message);
        string subject = message.Split('\n')[0];
        repo.UpdateHead(commitHash, $"commit{(amend ? " (amend)" : "")}: {subject}");

        if (mergeHead.HasValue)
            repo.Refs.ClearMergeHead();
        ConflictState.Clear(repo);

        Hooks.Run(repo, "post-commit", abortOnFailure: false);

        if (!quiet)
        {
            string branch = repo.Refs.GetCurrentBranch() ?? "(detached)";
            string shortHash = commitHash.Short;
            if (parents.Count > 1)
                Terminal.WriteSuccess($"[{branch} {shortHash}] Merge commit: {subject}");
            else
                Terminal.WriteSuccess($"[{branch} {shortHash}] {subject}");
        }

        return 0;
    }

    private static string ReadCommitMessage()
    {
        // Without an interactive console there is nothing to edit and no way to cancel:
        // launching an editor here blocks forever and deadlocks scripts and CI. Fail fast
        // with an actionable message instead.
        string? editor = Environment.GetEnvironmentVariable("SM_EDITOR")
                      ?? Environment.GetEnvironmentVariable("EDITOR")
                      ?? Environment.GetEnvironmentVariable("VISUAL");

        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (editor == null && !interactive)
        {
            Terminal.WriteError("no commit message supplied and no editor available in a non-interactive session");
            Terminal.WriteError("pass a message with -m <message>");
            return string.Empty;
        }

        string tmpFile = Path.Combine(Path.GetTempPath(), $"sm_commit_msg_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tmpFile,
                "\n# Please enter the commit message for your changes. Lines starting\n" +
                "# with '#' will be ignored, and an empty message aborts the commit.\n#\n" +
                "# On branch <branch>\n#\n" +
                "# Changes to be committed:\n#\n");

            editor ??= Helpers.IsWindows ? "notepad.exe" : "vi";

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = $"\"{tmpFile}\"",
                UseShellExecute = false
            });
            proc?.WaitForExit();

            string[] lines = File.ReadAllLines(tmpFile);
            var msgLines = lines.Where(l => !l.TrimStart().StartsWith('#')).ToList();

            string msg = string.Join("\n", msgLines).Trim();
            return msg;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }
}