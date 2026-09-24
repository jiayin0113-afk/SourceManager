namespace SourceManager;

/// <summary>
/// `sm rm` — remove files from the working tree and the index.
///
/// Safety is the point of this command: it refuses to delete a file with staged or unstaged
/// modifications, and refuses to delete content that the index has but HEAD does not, unless
/// --force is given. `--cached` removes only the index entry and leaves the file on disk.
/// </summary>
public static class RmCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool cached = args.GetBoolOption("cached");
        bool force = args.GetBoolOption("force");
        bool dryRun = args.GetBoolOption("dry-run");
        bool recursive = args.GetBoolOption("recursive");
        bool quiet = args.GetBoolOption("quiet");

        var paths = args.PositionalArgs;
        if (paths.Count == 0)
        {
            Terminal.WriteError("error: usage: sm rm [-r] [--cached] [--force] <file>...");
            return 1;
        }

        var status = repo.GetStatus();
        var targets = new List<(string Rel, string Full)>();

        foreach (string pattern in paths)
        {
            string full = Path.GetFullPath(pattern, repo.RootPath);
            if (!PathUtil.IsSubPath(repo.RootPath, full))
            {
                Terminal.WriteError($"fatal: '{pattern}' is outside the repository");
                return 1;
            }

            if (Directory.Exists(full))
            {
                if (!recursive)
                {
                    Terminal.WriteError($"fatal: not removing '{pattern}' recursively without -r");
                    return 1;
                }
                foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}.sm{Path.DirectorySeparatorChar}")) continue;
                    targets.Add((PathUtil.GetRelativePath(repo.RootPath, file), file));
                }
                continue;
            }

            if (!File.Exists(full))
            {
                // A path that is only in the index (already deleted on disk) is still removable.
                string rel = PathUtil.GetRelativePath(repo.RootPath, full);
                if (!repo.Index.Contains(rel))
                {
                    Terminal.WriteError($"fatal: pathspec '{pattern}' did not match any files");
                    return 1;
                }
                targets.Add((rel, full));
                continue;
            }

            targets.Add((PathUtil.GetRelativePath(repo.RootPath, full), full));
        }

        if (targets.Count == 0)
        {
            Terminal.WriteError("fatal: no files matched");
            return 1;
        }

        // Safety check first, so a multi-file rm is all-or-nothing rather than half-applied.
        if (!force)
        {
            var blocked = new List<(string Path, string Reason)>();
            foreach (var (rel, _) in targets)
            {
                if (!repo.Index.Contains(rel)) continue;

                if (status.Staged.ContainsKey(rel))
                    blocked.Add((rel, "changes staged in the index"));
                else if (status.Changes.ContainsKey(rel))
                    blocked.Add((rel, "local modifications"));
            }

            if (blocked.Count > 0)
            {
                Terminal.WriteError("error: the following files have changes that would be lost:");
                foreach (var (path, reason) in blocked)
                    Terminal.WriteError($"    {path}  ({reason})");
                Terminal.WriteError("hint: use --force to remove them anyway, or commit/stash first.");
                return 1;
            }
        }

        foreach (var (rel, full) in targets)
        {
            if (quiet == false || dryRun)
                Terminal.WriteLine($"rm '{rel}'");

            if (dryRun) continue;

            if (!cached && File.Exists(full))
            {
                try { File.Delete(full); }
                catch (Exception ex)
                {
                    Terminal.WriteError($"error: unable to delete '{rel}': {ex.Message}");
                    return 1;
                }
            }

            if (repo.Index.Contains(rel))
                repo.Index.Remove(rel);
        }

        if (!dryRun)
            repo.Index.Save();

        // Prune directories that the removal emptied.
        if (!cached && !dryRun)
        {
            foreach (string dir in targets
                         .Select(t => Path.GetDirectoryName(t.Rel))
                         .Where(d => !string.IsNullOrEmpty(d))
                         .Distinct()
                         .OrderByDescending(d => d!.Length))
            {
                string full = Path.Combine(repo.RootPath, dir!);
                if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length == 0)
                {
                    try { Directory.Delete(full); } catch { }
                }
            }
        }

        return 0;
    }
}
