namespace SourceManager;

public static class AddCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool all = args.GetBoolOption("all");
        bool update = args.GetBoolOption("update");
        bool force = args.GetBoolOption("force");
        bool dryRun = args.GetBoolOption("dry-run");
        bool verbose = args.GetBoolOption("verbose");
        bool intentToAdd = args.GetBoolOption("intent-to-add");

        var paths = args.PositionalArgs;

        if (all)
        {
            var status = repo.GetStatus();
            foreach (var (path, kind) in status.Changes.Where(c => c.Value != ChangeKind.Untracked && c.Value != ChangeKind.Ignored))
            {
                if (!dryRun)
                    repo.StageFile(path);
                if (verbose || dryRun)
                    Terminal.WriteLine($"add '{path}'", Terminal.Color.Green);
            }
            foreach (string path in status.Untracked)
            {
                if (!dryRun)
                    repo.StageFile(path);
                if (verbose || dryRun)
                    Terminal.WriteLine($"add '{path}'", Terminal.Color.Green);
            }
            repo.Index.Save();
            return 0;
        }

        if (update)
        {
            var tracked = repo.Index.GetTrackedFiles();
            foreach (var (path, _) in tracked)
            {
                if (!dryRun)
                {
                    string fullPath = Path.Combine(repo.RootPath, path);
                    if (File.Exists(fullPath))
                        repo.StageFile(path);
                    else
                        repo.Index.Remove(path);
                }
                if (verbose || dryRun)
                    Terminal.WriteLine($"add '{path}'", Terminal.Color.Green);
            }
            repo.Index.Save();
            return 0;
        }

        if (paths.Count == 0)
        {
            Terminal.WriteError("Nothing specified, nothing added.");
            Terminal.WriteWarning("Maybe you wanted to say 'sm add .'?");
            return 1;
        }

        foreach (string pattern in paths)
        {
            if (pattern == ".")
            {
                var status = repo.GetStatus();
                foreach (string path in status.Untracked)
                {
                    if (!repo.Ignore.IsIgnored(path, false) || force)
                    {
                        if (!dryRun)
                            repo.StageFile(path);
                        if (verbose || dryRun)
                            Terminal.WriteLine($"add '{path}'", Terminal.Color.Green);
                    }
                }
                foreach (var (path, kind) in status.Changes.Where(c => c.Value == ChangeKind.Modified || c.Value == ChangeKind.Deleted))
                {
                    if (!dryRun)
                    {
                        if (kind == ChangeKind.Deleted)
                            repo.Index.Remove(path);
                        else
                            repo.StageFile(path);
                    }
                    if (verbose || dryRun)
                        Terminal.WriteLine($"add '{path}'", Terminal.Color.Green);
                }
            }
            else
            {
                string fullPath = Path.GetFullPath(pattern, repo.RootPath);
                if (PathUtil.IsSubPath(repo.RootPath, fullPath))
                {
                    string relPath = PathUtil.GetRelativePath(repo.RootPath, fullPath);
                    if (File.Exists(fullPath))
                    {
                        if (!force && repo.Ignore.IsIgnored(relPath, false))
                        {
                            Terminal.WriteWarning($"'{relPath}' is ignored. Use --force to add.");
                            continue;
                        }
                        if (!dryRun)
                            repo.StageFile(relPath);
                        if (verbose || dryRun)
                            Terminal.WriteLine($"add '{relPath}'", Terminal.Color.Green);
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        foreach (string file in PathUtil.EnumerateAllFiles(fullPath))
                        {
                            string relFile = PathUtil.GetRelativePath(repo.RootPath, file);
                            if (!force && repo.Ignore.IsIgnored(relFile, false)) continue;
                            if (!dryRun)
                                repo.StageFile(relFile);
                            if (verbose || dryRun)
                                Terminal.WriteLine($"add '{relFile}'", Terminal.Color.Green);
                        }
                    }
                }
            }
        }

        repo.Index.Save();
        return 0;
    }
}