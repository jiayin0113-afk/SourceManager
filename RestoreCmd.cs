namespace SourceManager;

/// <summary>
/// `sm switch` and `sm restore` — the split-out forms of the two jobs `checkout` overloads.
///
/// Keeping them as thin, explicit aliases over the existing implementations means the semantics
/// stay identical to `checkout` (including the dirty-tree guard and the file-deletion behaviour)
/// while giving each operation a name that says what it does.
/// </summary>
public static class SwitchCmd
{
    public static int Execute(ParseResult args)
    {
        // `switch` never restores files, so a bare "switch -- x" is a usage error rather than a
        // silent checkout of the current branch.
        if (args.PassthroughArgs.Count > 0 && args.PositionalArgs.Count == 0)
        {
            Terminal.WriteError("fatal: 'switch' switches branches; use 'sm restore' to restore files");
            return 1;
        }

        return CheckoutCmd.Execute(args);
    }
}

public static class RestoreCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool staged = args.GetBoolOption("staged");
        bool worktree = args.GetBoolOption("worktree");
        bool sourceGiven = args.HasOption("source");
        string? source = args.GetOption("source") ?? args.GetOption("s");

        // Default to the working tree unless --staged was requested.
        if (!staged && !worktree) worktree = true;

        var paths = args.PositionalArgs;
        if (paths.Count == 0)
        {
            Terminal.WriteError("error: usage: sm restore [--staged] [--source <rev>] <path>...");
            return 1;
        }

        Hash from;
        if (source != null)
        {
            try
            {
                from = RevisionResolver.Resolve(repo, source).Commit;
            }
            catch (RevisionException ex)
            {
                Terminal.WriteError($"fatal: {ex.Message}");
                return 1;
            }
        }
        else
        {
            // Restoring the index restores from HEAD; restoring only the working tree restores
            // from the index, which is what actually makes "undo my edit" work.
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue || head.Value.Equals(Hash.Zero))
            {
                Terminal.WriteError("fatal: no HEAD commit to restore from");
                return 1;
            }
            from = head.Value;
        }

        Commit? commit = repo.Objects.ReadCommit(from);
        if (commit?.TreeHash.HasValue != true)
        {
            Terminal.WriteError("fatal: source has no tree");
            return 1;
        }
        var tree = repo.GetTreeEntries(commit.TreeHash.Value);

        int restored = 0;
        foreach (string pattern in paths)
        {
            string full = Path.GetFullPath(pattern, repo.RootPath);
            if (!PathUtil.IsSubPath(repo.RootPath, full))
            {
                Terminal.WriteError($"fatal: '{pattern}' is outside the repository");
                return 1;
            }
            string rel = PathUtil.GetRelativePath(repo.RootPath, full);

            // Restoring the working tree from the index (no --source, no --staged) must use the
            // INDEX content, not HEAD, or staged work would be silently discarded.
            if (worktree && !staged && source == null)
            {
                IndexEntry? entry = repo.Index.GetEntry(rel);
                if (entry == null)
                {
                    Terminal.WriteError($"error: pathspec '{pattern}' did not match any file in the index");
                    return 1;
                }

                byte[]? content = repo.Objects.ReadBlob(entry.ObjectHash);
                if (content == null)
                {
                    Terminal.WriteError($"error: object for '{rel}' is missing from the object database");
                    return 1;
                }

                WriteWorktree(repo, rel, content);
                restored++;
                continue;
            }

            if (!tree.TryGetValue(rel, out Hash blobId))
            {
                Terminal.WriteError($"error: pathspec '{pattern}' did not match any file in {from.Short}");
                return 1;
            }

            if (worktree)
            {
                byte[]? content = repo.Objects.ReadBlob(blobId);
                if (content == null)
                {
                    Terminal.WriteError($"error: object for '{rel}' is missing");
                    return 1;
                }
                WriteWorktree(repo, rel, content);
            }

            if (staged)
                repo.Index.Add(rel, blobId, FileMode.Normal);

            restored++;
        }

        if (staged)
            repo.Index.Save();

        Terminal.WriteSuccess($"Restored {restored} path(s).");
        return 0;
    }

    private static void WriteWorktree(Repository repo, string rel, byte[] content)
    {
        string full = Path.Combine(repo.RootPath, rel);
        string? dir = Path.GetDirectoryName(full);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
    }
}
