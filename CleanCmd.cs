namespace SourceManager;

/// <summary>
/// `sm clean` — remove untracked files from the working tree.
///
/// This is the only command that deletes files sm does not track, so it is deliberately
/// conservative: it does nothing without --force (or --dry-run), and it reports exactly what it
/// would remove. Ignored files are only touched with -x.
/// </summary>
public static class CleanCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool force = args.GetBoolOption("force");
        bool dryRun = args.GetBoolOption("dry-run");
        bool directories = args.GetBoolOption("directories");
        bool includeIgnored = args.GetBoolOption("ignored");
        bool quiet = args.GetBoolOption("quiet");
        bool json = JsonOut.Requested(args);

        if (!force && !dryRun)
        {
            Terminal.WriteError("fatal: clean.requireForce is true and neither -f nor -n was given; refusing to clean");
            return 1;
        }

        var status = repo.GetStatus();
        var removable = new List<string>();

        foreach (string path in status.Untracked)
        {
            if (repo.Ignore.IsIgnored(path, false) && !includeIgnored) continue;
            removable.Add(path);
        }

        if (includeIgnored)
        {
            // Ignored paths are excluded from Untracked, so enumerate the tree again to find them.
            foreach (string file in PathUtil.EnumerateAllFiles(repo.RootPath, repo.Ignore))
            {
                string rel = PathUtil.GetRelativePath(repo.RootPath, file);
                if (repo.Ignore.IsIgnored(rel, false) && !removable.Contains(rel))
                    removable.Add(rel);
            }
        }

        removable.Sort(StringComparer.Ordinal);

        if (json)
        {
            JsonOut.Write("clean", new
            {
                dryRun,
                count = removable.Count,
                paths = removable
            });
            if (dryRun) return 0;
        }

        int removed = 0;
        foreach (string rel in removable)
        {
            string full = Path.Combine(repo.RootPath, rel);
            if (!quiet)
                Terminal.WriteLine($"Removing {rel}");

            if (dryRun) continue;

            try
            {
                if (File.Exists(full)) File.Delete(full);
                removed++;
            }
            catch (Exception ex)
            {
                Terminal.WriteError($"warning: unable to remove '{rel}': {ex.Message}");
            }
        }

        if (!quiet && !json)
        {
            if (dryRun)
                Terminal.WriteLine($"[dry run] would remove {removable.Count} file(s).");
            else
                Terminal.WriteSuccess($"Removed {removed} file(s).");
        }

        // Empty directories are removed only when explicitly requested: silently pruning the
        // tree is surprising, and a directory may be intentionally empty.
        if (directories && !dryRun)
        {
            PruneEmptyDirectories(repo.RootPath, quiet);
        }

        return 0;
    }

    private static void PruneEmptyDirectories(string root, bool quiet)
    {
        void Walk(string dir)
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(sub) == ".sm") continue;
                Walk(sub);
                if (Directory.GetFileSystemEntries(sub).Length == 0)
                {
                    if (!quiet) Terminal.WriteLine($"Removing directory {PathUtil.GetRelativePath(root, sub)}");
                    try { Directory.Delete(sub); } catch { }
                }
            }
        }
        Walk(root);
    }
}
