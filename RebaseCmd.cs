using System.Text;

namespace SourceManager;

public static class RebaseCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool continueRebase = args.GetBoolOption("continue");
        bool abort = args.GetBoolOption("abort");
        bool skip = args.GetBoolOption("skip");
        bool interactive = args.GetBoolOption("interactive");
        string? ontoStr = args.GetOption("onto");
        bool preserveMerges = args.GetBoolOption("preserve-merges");
        bool root = args.GetBoolOption("root");
        string? strategy = args.GetOption("strategy");
        bool force = args.GetBoolOption("force");
        bool noFf = args.GetBoolOption("no-ff");
        bool autostash = args.GetBoolOption("autostash");
        bool autosquashRequested = args.GetBoolOption("autosquash");

        if (abort)
        {
            return AbortRebase(repo);
        }

        if (continueRebase)
        {
            return ContinueRebase(repo);
        }

        if (skip)
        {
            return SkipRebaseStep(repo);
        }

        string? upstream = args.GetPositional(0);
        string? ontoBranch = args.PositionalArgs.Count >= 2 ? args.PositionalArgs[1] : null;

        if (ontoStr != null) ontoBranch = ontoStr;

        if (upstream == null && !root)
        {
            Terminal.WriteError("error: upstream branch required");
            return 1;
        }

        // Resolve EVERY revision before touching the repository. A resolution failure after the
        // first mutation left a half-started rebase behind on a typo.
        Hash ontoHash;
        Hash? mergeBase;
        try
        {
            if (ontoBranch != null)
                ontoHash = ResolveHash(repo, ontoBranch);
            else if (upstream != null)
                ontoHash = ResolveHash(repo, upstream);
            else if (root)
                ontoHash = ResolveHash(repo, "HEAD");
            else
            {
                Terminal.WriteError("fatal: Need an upstream");
                return 1;
            }

            mergeBase = root ? null : ResolveHash(repo, upstream!);
        }
        catch (RevisionException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        try
        {
            string? currentBranch = repo.Refs.GetCurrentBranch();
            Hash? currentCommit = repo.Refs.GetHeadCommit();

            if (currentBranch == null)
            {
                Terminal.WriteError("fatal: You are in 'detached HEAD' state. Checkout a branch first.");
                return 1;
            }
            if (!currentCommit.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit");
                return 1;
            }

            // Refuse to silently destroy uncommitted work. CheckoutCommit rewrites the working
            // tree, so a dirty tree used to be reverted with no warning.
            var preStatus = repo.GetStatus();
            var dirtyPaths = preStatus.Changes.Keys.Concat(preStatus.Staged.Keys).Distinct().ToList();
            var dirtyUntracked = preStatus.Untracked.ToList();
            bool dirty = dirtyPaths.Count > 0 || dirtyUntracked.Count > 0;

            if (dirty && !autostash)
            {
                Terminal.WriteError("error: cannot rebase: you have unstaged changes.");
                Terminal.WriteError("hint: commit them, or re-run with --autostash to carry them across the rebase.");
                return 1;
            }

            repo.Refs.WriteRef("refs/rebase-original", currentCommit.Value);

            // --autostash: remember the dirty working tree, then restore it once the rebase ends
            // (successfully, or via --continue / --abort). The old code built a "stash" from the
            // index and never applied it, so the checkout below destroyed unstaged edits.
            if (dirty && autostash)
            {
                Autostash.Capture(repo, dirtyPaths);
                Terminal.WriteLine("Created autostash for your uncommitted changes.");
            }

            var commitsToRebase = new List<Hash>();

            // Enumerate directly from HEAD instead of via repo.GetCommitHistory(), which only
            // knows about the current branch: resolving "HEAD~2" against the branch ref made the
            // walk start one commit too far back and silently drop a commit from the plan.
            Hash? walker = currentCommit.Value;
            while (walker.HasValue && !walker.Value.Equals(Hash.Zero))
            {
                if (mergeBase.HasValue && walker.Value.Equals(mergeBase.Value)) break;
                commitsToRebase.Add(walker.Value);
                Commit? c = repo.Objects.ReadCommit(walker.Value);
                walker = c is { ParentHashes.Count: > 0 } ? c.ParentHashes[0] : null;
            }
            commitsToRebase.Reverse();

            if (commitsToRebase.Count == 0)
            {
                Terminal.WriteLine($"Current branch {currentBranch} is up to date.");
                return 0;
            }

            repo.CheckoutCommit(ontoHash);
            repo.Refs.SetBranch(currentBranch, ontoHash, $"rebase: checkout {ontoHash.Short}");

            // Persist the ordered remaining work so --continue / --skip can actually resume
            // instead of being stuck in the conflicted step forever.
            repo.Refs.WriteRebaseState(commitsToRebase);

            if (interactive || autosquashRequested)
            {
                return RunInteractive(repo, commitsToRebase, currentBranch!, ontoHash, interactive, autosquashRequested);
            }

            int step = 0;
            for (; step < commitsToRebase.Count; step++)
            {
                Hash cherryHash = commitsToRebase[step];
                Commit? cherryCommit = repo.Objects.ReadCommit(cherryHash);
                if (cherryCommit == null) continue;
                if (cherryCommit.ParentHashes.Count == 0) continue;

                Hash? currentHead = repo.Refs.GetHeadCommit();
                if (!currentHead.HasValue) continue;

                try
                {
                    ApplyStep(repo, cherryHash);
                }
                catch
                {
                    repo.Refs.WriteRebaseIndex(step);
                    return RebaseConflict(repo, cherryHash, step, commitsToRebase.Count);
                }

                // ApplyPatch writes conflict markers rather than throwing, so a conflicting step
                // must be detected here or the markers would be committed into history.
                var markers = FindConflictMarkers(repo);
                if (markers.Count > 0)
                {
                    repo.Refs.WriteRebaseIndex(step);
                    return RebaseConflict(repo, cherryHash, step, commitsToRebase.Count);
                }

                CommitStep(repo, cherryHash, cherryCommit.Message, $"{cherryHash.Short}");
            }

            FinishRebase(repo, currentBranch, ontoHash);
            Terminal.WriteSuccess($"Successfully rebased {currentBranch} onto {ontoHash.Short}.");
            return 0;
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: rebase failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Applies one commit's change to the current index.
    ///
    /// The diff is taken against the commit's ORIGINAL parent and applied onto wherever the
    /// rebase currently is, which is what lets steps be reordered, squashed and dropped.
    ///
    /// When the patch does not apply cleanly (its context no longer matches) the change is
    /// replayed as a three-way replacement from the commit's own tree. That is the correct
    /// semantics for a rebase step — the step says "the file ends up like this" — and it keeps
    /// `pick` from silently producing a wrong file.
    /// </summary>
    private static void ApplyStep(Repository repo, Hash commitHash)
    {
        Commit? commit = repo.Objects.ReadCommit(commitHash);
        if (commit == null)
            throw new InvalidOperationException($"commit {commitHash.Short} is missing");
        if (commit.ParentHashes.Count == 0)
            throw new InvalidOperationException($"commit {commitHash.Short} has no parent to diff against");

        Commit? parent = repo.Objects.ReadCommit(commit.ParentHashes[0]);
        if (parent?.TreeHash.HasValue != true || !commit.TreeHash.HasValue)
            throw new InvalidOperationException($"commit {commitHash.Short} is missing a tree");

        var baseFiles = repo.GetTreeEntries(parent.TreeHash.Value);
        var theirFiles = repo.GetTreeEntries(commit.TreeHash.Value);

        // Only paths this step actually changed are touched; everything else is left as the
        // rebase built it.
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, theirHash) in theirFiles)
        {
            if (baseFiles.TryGetValue(path, out Hash baseHash) && baseHash.Equals(theirHash)) continue;
            touched.Add(path);
        }
        foreach (var (path, _) in baseFiles)
        {
            if (!theirFiles.ContainsKey(path)) touched.Add(path);
        }

        foreach (string path in touched)
        {
            string full = Path.Combine(repo.RootPath, path);
            bool inBase = baseFiles.TryGetValue(path, out Hash baseHash);
            bool inTheirs = theirFiles.TryGetValue(path, out Hash theirHash);

            if (!inTheirs)
            {
                // Deleted by this step.
                if (File.Exists(full)) File.Delete(full);
                repo.Index.Remove(path);
                continue;
            }

            byte[]? theirContent = repo.Objects.ReadBlob(theirHash);
            if (theirContent == null)
                throw new InvalidOperationException($"blob for '{path}' is missing from the object database");

            // A path this step only ADDS cannot conflict with anything: write it verbatim.
            if (!inBase)
            {
                WriteFile(repo, full, theirContent);
                repo.StageFile(path);
                continue;
            }

            byte[]? baseContent = repo.Objects.ReadBlob(baseHash);
            byte[]? ourContent = File.Exists(full) ? File.ReadAllBytes(full) : null;

            // Our side still matches the step's parent, so the step's version is the answer.
            if (ourContent == null || ContentsEqual(ourContent, baseContent))
            {
                WriteFile(repo, full, theirContent);
                repo.StageFile(path);
                continue;
            }

            // Our side already equals the step's version.
            if (ContentsEqual(ourContent, theirContent))
            {
                repo.StageFile(path);
                continue;
            }

            // Both sides changed the same file: merge. Binary content cannot be line-merged, so
            // the step's version wins, which matches what a checkout would produce.
            if (Helpers.IsBinary(theirContent) || (baseContent != null && Helpers.IsBinary(baseContent)))
            {
                WriteFile(repo, full, theirContent);
                repo.StageFile(path);
                continue;
            }

            string baseText = baseContent != null ? Encoding.UTF8.GetString(baseContent) : "";
            var merged = repo.Merge.ThreeWayMerge(baseText, Encoding.UTF8.GetString(ourContent),
                Encoding.UTF8.GetString(theirContent), path);
            File.WriteAllText(full, merged.MergedText);
            repo.StageFile(path);
        }
    }

    private static bool ContentsEqual(byte[] a, byte[]? b)
        => b != null && a.AsSpan().SequenceEqual(b);

    private static void WriteFile(Repository repo, string full, byte[] content)
    {
        string? dir = Path.GetDirectoryName(full);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
    }

    /// <summary>
    /// Records the staged tree as a commit on top of the current branch, optionally replacing the
    /// message. Squash/fixup use the accumulated message instead of the step's own.
    /// </summary>
    private static Hash CommitStep(Repository repo, Hash originalCommit, string message, string reflogLabel)
    {
        Hash? head = repo.Refs.GetHeadCommit();
        if (!head.HasValue) throw new InvalidOperationException("rebase lost HEAD");

        Hash treeHash = repo.WriteTreeFromIndex();
        Hash newCommit = repo.CreateCommit(treeHash, new List<Hash> { head.Value }, message);

        string? branch = repo.Refs.GetCurrentBranch();
        if (branch != null)
            repo.Refs.SetBranch(branch, newCommit, $"rebase: {reflogLabel} {message.Split('\n')[0]}");
        else
            repo.Refs.SetHeadDetached(newCommit);

        return newCommit;
    }

    /// <summary>
    /// Runs an interactive (or --autosquash) rebase: build a plan, let the user edit it, then
    /// execute every step, persisting position after each one.
    /// </summary>
    private static int RunInteractive(Repository repo, List<Hash> commits, string branch, Hash ontoHash,
        bool interactive, bool autosquash)
    {
        var steps = new List<RebaseStep>();
        foreach (Hash hash in commits)
        {
            Commit? commit = repo.Objects.ReadCommit(hash);
            if (commit == null) continue;
            steps.Add(new RebaseStep(RebaseOp.Pick, hash, commit.Message.Split('\n')[0]));
        }

        if (steps.Count == 0)
        {
            Terminal.WriteLine("Nothing to do.");
            FinishRebase(repo, branch, ontoHash);
            return 0;
        }

        if (autosquash)
            steps = RebasePlan.Autosquash(steps);

        if (interactive)
        {
            if (!RebasePlan.TryEdit(repo, ref steps, out string? error))
            {
                Terminal.WriteError($"error: {error}");
                Terminal.WriteError("hint: run 'sm rebase --abort' to undo the rebase.");
                return 1;
            }
        }


        RebasePlan.Save(repo, steps);
        // Start at the FIRST step: the rebase has only checked out the onto commit so far, and
        // passing 1 here silently skipped whatever the user put first in the plan.
        return ExecutePlan(repo, steps, 0, branch, null);
    }

    /// <summary>
    /// Executes the plan from <paramref name="startIndex"/>. A squash/fixup run accumulates into
    /// <paramref name="pendingMessage"/> and only commits when the run ends.
    /// </summary>
    private static int ExecutePlan(Repository repo, List<RebaseStep> steps, int startIndex,
        string branch, string? pendingMessage)
    {
        // A local accumulator is required: assigning to the PARAMETER on one iteration did not
        // survive the `continue`.
        string? squashBuffer = pendingMessage;

        // Whether the change currently in the index has already been recorded as a commit.
        // A following squash/fixup needs its own commit to meld into, so this flag — not the
        // buffer — is what decides whether to emit one first.
        bool currentCommitted = pendingMessage != null;

        for (int i = startIndex; i < steps.Count; i++)
        {
            RebaseStep step = steps[i];
            repo.Refs.WriteRebaseIndex(i);

            switch (step.Op)
            {
                case RebaseOp.Drop:
                    Terminal.WriteLine($"Dropped {step.Commit.Short} {step.Subject}");
                    continue;

                case RebaseOp.Exec:
                    // exec is recorded but not executed: running arbitrary commands as part of a
                    // rebase is a deliberate choice we do not make for the user.
                    Terminal.WriteWarning($"skipping exec step for {step.Commit.Short} (exec is not supported)");
                    continue;
            }

            Commit? commit = repo.Objects.ReadCommit(step.Commit);
            if (commit == null)
            {
                Terminal.WriteError($"error: commit {step.Commit.Short} is missing; cannot continue");
                return 1;
            }

            try
            {
                ApplyStep(repo, step.Commit);
            }            catch (Exception ex)
            {
                Terminal.WriteError($"error: could not apply {step.Commit.Short}: {ex.Message}");
                return RebaseConflict(repo, step.Commit, i, steps.Count);
            }


            var markers = FindConflictMarkers(repo);
            if (markers.Count > 0)
            {
                Terminal.WriteError("error: the step produced conflicts");
                foreach (string path in markers)
                    Terminal.WriteError($"    {path}");
                return RebaseConflict(repo, step.Commit, i, steps.Count);
            }


            string stepMessage = commit.Message;

            switch (step.Op)
            {
                case RebaseOp.Squash:
                case RebaseOp.Fixup:
                {
                    if (!currentCommitted)
                    {
                        // This change has not been recorded yet. A squash/fixup melds into the
                        // PREVIOUS commit, so that one is emitted now and this change folds in.
                        CommitStep(repo, step.Commit, stepMessage, step.Commit.Short);
                        squashBuffer = stepMessage;
                        currentCommitted = true;
                        continue;
                    }

                    // Fold into the commit that is already there.
                    squashBuffer = step.Op == RebaseOp.Squash && squashBuffer != null
                        ? ComposeSquashMessage(squashBuffer, stepMessage)
                        : squashBuffer ?? stepMessage;

                    Hash? head = repo.Refs.GetHeadCommit();
                    Commit? previous = head.HasValue ? repo.Objects.ReadCommit(head.Value) : null;
                    if (previous == null)
                    {
                        Terminal.WriteError("error: nothing to squash into");
                        return 1;
                    }

                    Hash treeHash = repo.WriteTreeFromIndex();
                    var parents = new List<Hash>(previous.ParentHashes);
                    Hash folded = repo.CreateCommit(treeHash, parents, squashBuffer);
                    repo.Refs.SetBranch(branch, folded, $"rebase: squash {step.Commit.Short}");
                    continue;
                }

                case RebaseOp.Reword:
                {
                    string? edited = EditMessage(stepMessage);
                    if (edited == null)
                    {
                        Terminal.WriteError("error: empty commit message; use 'sm rebase --skip' to drop this commit");
                        return RebaseConflict(repo, step.Commit, i, steps.Count);
                    }
                    CommitStep(repo, step.Commit, edited, step.Commit.Short);
                    squashBuffer = edited;
                    currentCommitted = true;
                    continue;
                }

                case RebaseOp.Edit:
                {
                    CommitStep(repo, step.Commit, stepMessage, step.Commit.Short);
                    currentCommitted = true;
                    squashBuffer = stepMessage;
                    // Stop so the user can amend; --continue resumes from the next step.
                    repo.Refs.WriteRebaseIndex(i + 1);
                    Terminal.WriteLine($"Stopped at {step.Commit.Short}... {step.Subject}");
                    Terminal.WriteLine("You can amend the commit now, then run 'sm rebase --continue'.");
                    return 0;
                }

                default:
                {
                    CommitStep(repo, step.Commit, stepMessage, step.Commit.Short);
                    currentCommitted = true;
                    squashBuffer = stepMessage;
                    continue;
                }
            }
        }

        RebasePlan.Clear(repo);
        FinishRebase(repo, branch, repo.Refs.GetHeadCommit() ?? Hash.Zero);
        Terminal.WriteSuccess("Interactive rebase complete.");
        return 0;
    }

    /// <summary>Combines messages for squash, keeping both bodies.</summary>
    private static string ComposeSquashMessage(string first, string second)
    {
        return $"{first.TrimEnd()}\n\n{second.Trim()}";
    }

    /// <summary>Opens the editor on a single commit message.</summary>
    private static string? EditMessage(string original)
    {
        string? editor = Environment.GetEnvironmentVariable("SM_EDITOR")
                      ?? Environment.GetEnvironmentVariable("EDITOR")
                      ?? Environment.GetEnvironmentVariable("VISUAL");

        bool interactiveConsole = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (editor == null && !interactiveConsole) return original;

        string tmp = Path.Combine(Path.GetTempPath(), $"sm-reword-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, original);
            editor ??= Helpers.IsWindows ? "notepad.exe" : "vi";

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = $"\"{tmp}\"",
                UseShellExecute = false
            });
            proc?.WaitForExit();

            string edited = File.ReadAllText(tmp).Trim();
            return edited.Length == 0 ? null : edited;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Reports a stopped rebase step and leaves the state on disk for --continue.</summary>
    private static int RebaseConflict(Repository repo, Hash cherryHash, int step, int total)
    {
        Terminal.WriteError($"error: could not apply {cherryHash.Short}...");
        Terminal.WriteLine($"Resolve all conflicts manually, mark them as resolved with 'sm add',");
        Terminal.WriteLine($"then run 'sm rebase --continue'. ({step + 1}/{total} steps applied)");
        Terminal.WriteLine($"Use 'sm rebase --skip' to drop this commit or 'sm rebase --abort' to undo.");
        return 1;
    }

    /// <summary>Clears rebase state and puts back any autostashed work.</summary>
    private static void FinishRebase(Repository repo, string? branch, Hash ontoHash)
    {
        repo.Refs.ClearRebaseState();
        int restored = Autostash.Restore(repo);
        if (restored > 0)
            Terminal.WriteLine($"Applied autostash ({restored} path(s) restored).");
    }

    private static void ApplyPatch(Repository repo, List<FileDiff> diffs)
    {
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
    }

    private static int AbortRebase(Repository repo)
    {
        // The original HEAD is recorded with WriteRef (which maps to .sm/refs/rebase-original),
        // so it must be read back through the same namespace. Reading it as a BRANCH looked in
        // refs/heads/ and always reported "no rebase in progress", leaving a conflicted rebase
        // impossible to undo.
        Hash? originalHead = repo.Refs.ResolveRefName("refs/rebase-original");
        if (originalHead == null)
        {
            Terminal.WriteError("error: no rebase in progress");
            return 1;
        }

        repo.CheckoutCommit(originalHead.Value);
        repo.Index.Clear();
        repo.Index.Save();

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (currentBranch != null)
            repo.Refs.SetBranch(currentBranch, originalHead.Value, "rebase: abort");

        repo.Refs.ClearRebaseState();
        RebasePlan.Clear(repo);
        int restored = Autostash.Restore(repo);
        if (restored > 0)
            Terminal.WriteLine($"Applied autostash ({restored} path(s) restored).");

        Terminal.WriteSuccess("Rebase aborted.");
        return 0;
    }

    private static int ContinueRebase(Repository repo)
    {
        if (!repo.Refs.RebaseInProgress)
        {
            Terminal.WriteError("error: no rebase in progress");
            return 1;
        }

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (currentBranch == null)
        {
            Terminal.WriteError("fatal: no current branch");
            return 1;
        }

        // Refuse to commit a half-resolved conflict: doing so wrote the conflict markers into
        // history, which is worse than failing.
        var markers = FindConflictMarkers(repo);
        if (markers.Count > 0)
        {
            Terminal.WriteError("error: you still have unresolved conflicts in:");
            foreach (string path in markers)
                Terminal.WriteError($"    {path}");
            Terminal.WriteError("hint: resolve them, then 'sm add <file>' and 'sm rebase --continue'.");
            return 1;
        }

        // An interactive rebase has a richer plan; resume by executing it rather than by the
        // flat todo list, so squash/reword/edit/drop survive the interruption.
        if (RebasePlan.Exists(repo))
        {
            var plan = RebasePlan.Load(repo);
            int planIndex = repo.Refs.GetRebaseIndex();

            if (planIndex >= plan.Count)
            {
                RebasePlan.Clear(repo);
                FinishRebase(repo, currentBranch, repo.Refs.GetHeadCommit() ?? Hash.Zero);
                Terminal.WriteSuccess("Rebase finished.");
                return 0;
            }

            // The step we stopped on was already applied (edit/reword/conflict), so commit the
            // index as that step before moving on.
            RebaseStep current = plan[planIndex];
            if (current.Op is RebaseOp.Edit or RebaseOp.Pick or RebaseOp.Reword)
            {
                Commit? commit = repo.Objects.ReadCommit(current.Commit);
                if (commit != null)
                {
                    Hash? head = repo.Refs.GetHeadCommit();
                    // Only commit when there is actually something new staged.
                    Hash treeHash = repo.WriteTreeFromIndex();
                    Commit? previous = head.HasValue ? repo.Objects.ReadCommit(head.Value) : null;
                    if (previous?.TreeHash.HasValue != true || !previous.TreeHash.Value.Equals(treeHash))
                    {
                        CommitStep(repo, current.Commit, commit.Message, $"{current.Commit.Short} (resumed)");
                    }
                }
            }

            return ExecutePlan(repo, plan, planIndex + 1, currentBranch, null);
        }

        var todo = repo.Refs.GetRebaseTodo();
        int index = repo.Refs.GetRebaseIndex();
        if (index >= todo.Count)
        {
            FinishRebase(repo, currentBranch, repo.Refs.GetHeadCommit() ?? Hash.Zero);
            Terminal.WriteSuccess("Rebase finished.");
            return 0;
        }

        return ReplayRemaining(repo, currentBranch, todo, index);
    }

    /// <summary>
    /// Commits the resolved step and replays every remaining commit in the todo list.
    /// The old --continue wrote a single commit and stopped, leaving the rebase permanently
    /// half-finished with no way to reach the rest of the queue.
    /// </summary>
    private static int ReplayRemaining(Repository repo, string currentBranch, List<Hash> todo, int startIndex)
    {
        Commit? stepCommit = repo.Objects.ReadCommit(todo[startIndex]);
        Hash? head = repo.Refs.GetHeadCommit();
        if (head.HasValue && stepCommit != null)
        {
            Hash treeHash = repo.WriteTreeFromIndex();
            Hash committed = repo.CreateCommit(treeHash, new List<Hash> { head.Value }, stepCommit.Message);
            repo.Refs.SetBranch(currentBranch, committed, $"rebase: continue {todo[startIndex].Short}");
        }

        for (int step = startIndex + 1; step < todo.Count; step++)
        {
            var cherryCommit = repo.Objects.ReadCommit(todo[step]);
            if (cherryCommit == null || cherryCommit.ParentHashes.Count == 0) continue;

            Hash? currentHead = repo.Refs.GetHeadCommit();
            if (!currentHead.HasValue) break;

            var diffs = repo.Diff.DiffCommits(cherryCommit.ParentHashes[0], todo[step]);
            try
            {
                ApplyPatch(repo, diffs);
            }
            catch
            {
                repo.Refs.WriteRebaseIndex(step);
                return RebaseConflict(repo, todo[step], step, todo.Count);
            }

            Hash treeHash = repo.WriteTreeFromIndex();
            Hash newCommit = repo.CreateCommit(treeHash, new List<Hash> { currentHead.Value }, cherryCommit.Message);
            repo.Refs.SetBranch(currentBranch, newCommit,
                $"rebase: {todo[step].Short} {cherryCommit.Message.Split('\n')[0]}");
        }

        Hash finalHead = repo.Refs.GetHeadCommit() ?? Hash.Zero;
        FinishRebase(repo, currentBranch, finalHead);
        Terminal.WriteSuccess("Rebase continued to completion.");
        return 0;
    }

    /// <summary>Paths in the index whose content still contains conflict markers.</summary>
    private static List<string> FindConflictMarkers(Repository repo)
    {
        var result = new List<string>();
        foreach (var (path, _) in repo.Index.GetAllEntries())
        {
            string full = Path.Combine(repo.RootPath, path);
            if (!File.Exists(full)) continue;
            try
            {
                foreach (string line in File.ReadLines(full))
                {
                    if (line.StartsWith("<<<<<<<", StringComparison.Ordinal) ||
                        line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                    {
                        result.Add(path);
                        break;
                    }
                }
            }
            catch
            {
                // Unreadable content is not a conflict; ignore.
            }
        }
        return result;
    }

    private static int SkipRebaseStep(Repository repo)
    {
        if (!repo.Refs.RebaseInProgress)
        {
            Terminal.WriteError("error: no rebase in progress");
            return 1;
        }

        string? currentBranch = repo.Refs.GetCurrentBranch();
        if (currentBranch == null)
        {
            Terminal.WriteError("fatal: no current branch");
            return 1;
        }

        var todo = repo.Refs.GetRebaseTodo();
        int index = repo.Refs.GetRebaseIndex();
        if (index < todo.Count)
        {
            Commit? skipped = repo.Objects.ReadCommit(todo[index]);
            Terminal.WriteLine($"Skipping commit: {skipped?.Message.Split('\n')[0] ?? todo[index].Short}");
        }

        // Move past the skipped step and keep going; previously this printed a message and
        // returned, leaving the rebase wedged on the same commit.
        int next = index + 1;
        if (next >= todo.Count)
        {
            FinishRebase(repo, currentBranch, repo.Refs.GetHeadCommit() ?? Hash.Zero);
            Terminal.WriteSuccess("Rebase finished (last step skipped).");
            return 0;
        }

        repo.Refs.WriteRebaseIndex(next);
        return ReplayRemaining(repo, currentBranch, todo, next);
    }

    /// <summary>
    /// Revision resolution is delegated to the shared resolver, so rebase accepts the same
    /// syntax as every other command — including remote-tracking refs like origin/main.
    /// </summary>
    private static Hash ResolveHash(Repository repo, string input)
        => RevisionResolver.Resolve(repo, input).Commit;
}
