namespace SourceManager;

public static class StatusCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool shortFmt = args.GetBoolOption("short");
        bool branch = args.GetBoolOption("branch") || !shortFmt;
        bool verbose = args.GetBoolOption("verbose");
        bool ignored = args.GetBoolOption("ignored");
        bool json = JsonOut.Requested(args);
        bool porcelain = args.GetBoolOption("porcelain") || args.GetBoolOption("porcelain-v2");

        var status = repo.GetStatus();

        if (json)
        {
            JsonOut.Write("status", new
            {
                branch = status.CurrentBranch,
                upstream = status.UpstreamBranch,
                ahead = status.Ahead,
                behind = status.Behind,
                detached = repo.Refs.IsDetached(),
                clean = status.IsClean,
                staged = status.Staged
                    .OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => JsonOut.FileChange(c.Key, c.Value))
                    .ToList(),
                unstaged = status.Changes
                    .OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => JsonOut.FileChange(c.Key, c.Value))
                    .ToList(),
                untracked = status.Untracked.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                conflicted = status.Conflicted.OrderBy(p => p, StringComparer.Ordinal).ToList()
            });
            return 0;
        }

        // --porcelain is the stable machine format: two fixed status columns, tab-separated,
        // no colour, no prose, no locale-dependent text. Scripts parse this; humans read the
        // long form.
        if (porcelain)
        {
            PrintPorcelain(repo, status);
            return 0;
        }

        if (shortFmt)
        {
            PrintShortStatus(repo, status);
            if (verbose && branch)
                PrintBranchInfo(repo, status);
        }
        else
        {
            PrintLongStatus(repo, status, ignored);
        }

        return 0;
    }

    private static char ShortCode(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 'A',
        ChangeKind.Modified => 'M',
        ChangeKind.Deleted => 'D',
        ChangeKind.Renamed => 'R',
        ChangeKind.Copied => 'C',
        ChangeKind.TypeChanged => 'T',
        ChangeKind.Unmerged => 'U',
        _ => ' '
    };

    /// <summary>
    /// Two-column XY status: X is index vs HEAD, Y is worktree vs index.
    /// </summary>
    private static void PrintShortStatus(Repository repo, WorkingTreeStatus status)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(status.Staged.Keys);
        paths.UnionWith(status.Changes.Keys);

        foreach (string path in paths)
        {
            char x = status.Staged.TryGetValue(path, out ChangeKind staged) ? ShortCode(staged) : ' ';
            char y = status.Changes.TryGetValue(path, out ChangeKind work) ? ShortCode(work) : ' ';
            Terminal.WriteLine($"{x}{y} {path}");
        }

        foreach (string path in status.Untracked.OrderBy(p => p, StringComparer.Ordinal))
        {
            Terminal.WriteLine($"?? {path}", Terminal.Color.Red);
        }
    }

    /// <summary>
    /// Stable machine format: "XY path" per line, matching the two-column short format but with
    /// no colour and no truncation. This is the contract scripts may depend on.
    /// </summary>
    private static void PrintPorcelain(Repository repo, WorkingTreeStatus status)
    {
        if (status.CurrentBranch != null)
        {
            Console.WriteLine(repo.Refs.IsDetached()
                ? $"## HEAD (detached)"
                : $"## {status.CurrentBranch}");
        }

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(status.Staged.Keys);
        paths.UnionWith(status.Changes.Keys);
        paths.UnionWith(status.Conflicted);

        foreach (string path in paths)
        {
            char x = status.Conflicted.Contains(path)
                ? 'U'
                : status.Staged.TryGetValue(path, out ChangeKind staged) ? ShortCode(staged) : ' ';
            char y = status.Conflicted.Contains(path)
                ? 'U'
                : status.Changes.TryGetValue(path, out ChangeKind work) ? ShortCode(work) : ' ';
            Console.WriteLine($"{x}{y} {path}");
        }

        foreach (string path in status.Untracked.OrderBy(p => p, StringComparer.Ordinal))
            Console.WriteLine($"?? {path}");
    }

    private static void PrintLongStatus(Repository repo, WorkingTreeStatus status, bool showIgnored)
    {
        Terminal.WriteLine($"On branch {status.CurrentBranch}", Terminal.Color.BrightCyan, true);

        if (status.UpstreamBranch != null)
        {
            string tracking = $"Your branch and '{status.UpstreamBranch}' have diverged.";
            if (status.Ahead > 0 && status.Behind > 0)
                tracking = $"Your branch is ahead by {status.Ahead} and behind by {status.Behind} commit(s).";
            else if (status.Ahead > 0)
                tracking = $"Your branch is ahead of '{status.UpstreamBranch}' by {status.Ahead} commit(s).";
            else if (status.Behind > 0)
                tracking = $"Your branch is behind '{status.UpstreamBranch}' by {status.Behind} commit(s).";
            else
                tracking = $"Your branch is up to date with '{status.UpstreamBranch}'.";
            Console.WriteLine(tracking);
            Console.WriteLine();
        }

        bool hasChanges = false;

        if (status.Conflicted.Count > 0)
        {
            Terminal.WriteLine("Unmerged paths:", Terminal.Color.Red, true);
            Terminal.WriteLine("  (use \"sm add <file>...\" to mark resolution)");
            Console.WriteLine();
            foreach (string path in status.Conflicted.OrderBy(p => p, StringComparer.Ordinal))
            {
                Terminal.WriteLine($"        both modified:   {path}", Terminal.Color.Red);
            }
            Console.WriteLine();
            hasChanges = true;
        }

        if (status.Staged.Count > 0)
        {
            Terminal.WriteLine("Changes to be committed:", Terminal.Color.Green, true);
            Terminal.WriteLine("  (use \"sm reset HEAD <file>...\" to unstage)");
            Console.WriteLine();
            foreach (var (path, kind) in status.Staged.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                string label = kind switch
                {
                    ChangeKind.Modified => "modified:",
                    ChangeKind.Added => "new file:",
                    ChangeKind.Deleted => "deleted:",
                    ChangeKind.Renamed => "renamed:",
                    ChangeKind.Copied => "copied:",
                    ChangeKind.TypeChanged => "typechange:",
                    _ => "modified:"
                };
                Terminal.Write($"        {label}   ", Terminal.Color.Green);
                Console.WriteLine(path);
            }
            Console.WriteLine();
            hasChanges = true;
        }

        if (status.Changes.Count > 0)
        {
            Terminal.WriteLine("Changes not staged for commit:", Terminal.Color.Red, true);
            Terminal.WriteLine("  (use \"sm add <file>...\" to update what will be committed)");
            Console.WriteLine();
            foreach (var (path, kind) in status.Changes.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                string label = kind switch
                {
                    ChangeKind.Modified => "modified:",
                    ChangeKind.Deleted => "deleted:",
                    ChangeKind.Added => "new file:",
                    ChangeKind.TypeChanged => "typechange:",
                    _ => "modified:"
                };
                Terminal.Write($"        {label}   ", Terminal.Color.Red);
                Console.WriteLine(path);
            }
            Console.WriteLine();
            hasChanges = true;
        }

        if (status.Untracked.Count > 0)
        {
            Terminal.WriteLine("Untracked files:", Terminal.Color.Red, true);
            Terminal.WriteLine("  (use \"sm add <file>...\" to include in what will be committed)");
            Console.WriteLine();
            foreach (string path in status.Untracked.OrderBy(p => p, StringComparer.Ordinal))
            {
                Console.WriteLine($"        {path}");
            }
            Console.WriteLine();
            hasChanges = true;
        }

        if (!hasChanges)
        {
            Terminal.WriteLine("nothing to commit, working tree clean");
        }
    }

    private static void PrintBranchInfo(Repository repo, WorkingTreeStatus status)
    {
        Console.WriteLine($"## {status.CurrentBranch}");
        if (status.UpstreamBranch != null)
            Console.WriteLine($"## ...{status.UpstreamBranch}");
    }
}