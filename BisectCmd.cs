namespace SourceManager;

public static class BisectCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool start = args.GetBoolOption("start");
        bool good = args.GetBoolOption("good");
        bool bad = args.GetBoolOption("bad");
        bool skip = args.GetBoolOption("skip");
        bool reset = args.GetBoolOption("reset");
        bool visualize = args.GetBoolOption("visualize");
        bool replay = args.GetBoolOption("replay");
        bool run = args.GetPositional(0) == "run";
        string? log = args.GetPositional(0) == "log" ? args.GetPositional(0) : null;

        if (reset)
        {
            repo.Refs.ResetBisect();
            Terminal.WriteSuccess("Bisect reset.");
            return 0;
        }

        if (log != null)
        {
            string[] entries = repo.Refs.GetBisectLog();
            foreach (string entry in entries)
                Console.WriteLine(entry);
            return 0;
        }

        if (visualize)
        {
            Terminal.WriteLine("Bisect visualization:");
            string[] entries = repo.Refs.GetBisectLog();
            foreach (string entry in entries)
            {
                if (entry.StartsWith("# bad:"))
                {
                    string hash = entry[6..].Trim();
                    Terminal.Write($"[BAD]  ", Terminal.Color.Red);
                    Console.WriteLine(hash);
                }
                else if (entry.StartsWith("# good:"))
                {
                    string hash = entry[7..].Trim();
                    Terminal.Write($"[GOOD] ", Terminal.Color.Green);
                    Console.WriteLine(hash);
                }
                else if (entry.StartsWith("# skip:"))
                {
                    string hash = entry[7..].Trim();
                    Terminal.Write($"[SKIP] ", Terminal.Color.Yellow);
                    Console.WriteLine(hash);
                }
                else
                {
                    Console.WriteLine(entry);
                }
            }
            return 0;
        }

        if (run)
        {
            Terminal.WriteError("fatal: 'sm bisect run' not yet implemented");
            return 1;
        }

        // GetPositional(-1) indexed the list with a negative number and threw
        // IndexOutOfRangeException; read through the bounds-safe accessor instead.
        string? revision = args.PositionalArgs.Count > 0 ? args.PositionalArgs[0] : null;

        if (start)
        {
            if (args.PositionalArgs.Count < 2)
            {
                Terminal.WriteError("error: bisect start requires <bad> <good> [<good>...]");
                return 1;
            }
            return StartBisect(repo, args.PositionalArgs);
        }

        if (good)
        {
            return MarkGood(repo, revision);
        }

        if (bad)
        {
            return MarkBad(repo, revision);
        }

        if (skip)
        {
            if (revision == null) revision = "HEAD";
            MarkSkip(repo, revision);
        }

        return CheckBisectProgress(repo);
    }

    private static int StartBisect(Repository repo, List<string> revisions)
    {
        repo.Refs.ResetBisect();

        try
        {
            Hash badHash = ResolveHash(repo, revisions[0]);
            MarkBad(repo, revisions[0]);

            for (int i = 1; i < revisions.Count; i++)
            {
                MarkGood(repo, revisions[i]);
            }

            return CheckBisectProgress(repo);
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }
    }

    private static int MarkGood(Repository repo, string? revision)
    {
        try
        {
            Hash hash = revision != null ? ResolveHash(repo, revision) : repo.Refs.GetHeadCommit()!.Value;
            repo.Refs.SetBisectLog($"# good: {hash.ToHex()}");

            Terminal.WriteLine($"Marked as good: {hash.Short}");
            return CheckBisectProgress(repo);
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }
    }

    private static int MarkBad(Repository repo, string? revision)
    {
        try
        {
            Hash hash = revision != null ? ResolveHash(repo, revision) : repo.Refs.GetHeadCommit()!.Value;
            repo.Refs.SetBisectLog($"# bad: {hash.ToHex()}");

            repo.CheckoutCommit(hash);
            repo.Refs.SetHeadDetached(hash);

            Terminal.WriteLine($"Marked as bad: {hash.Short}");
            return CheckBisectProgress(repo);
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }
    }

    private static void MarkSkip(Repository repo, string revision)
    {
        try
        {
            Hash hash = ResolveHash(repo, revision);
            repo.Refs.SetBisectLog($"# skip: {hash.ToHex()}");
            Terminal.WriteLine($"Marked as skip: {hash.Short}");
        }
        catch { }
    }

    private static int CheckBisectProgress(Repository repo)
    {
        string[] entries = repo.Refs.GetBisectLog();
        var badHashes = new List<Hash>();
        var goodHashes = new List<Hash>();
        var skipHashes = new HashSet<Hash>();

        foreach (string entry in entries)
        {
            if (entry.StartsWith("# bad:"))
                badHashes.Add(Hash.Parse(entry[6..].Trim()));
            else if (entry.StartsWith("# good:"))
                goodHashes.Add(Hash.Parse(entry[7..].Trim()));
            else if (entry.StartsWith("# skip:"))
                skipHashes.Add(Hash.Parse(entry[7..].Trim()));
        }

        if (badHashes.Count == 0 || goodHashes.Count == 0)
        {
            Terminal.WriteLine("status: waiting for both good and bad commits");
            return 0;
        }

        var reachableFromBads = new HashSet<string>();
        foreach (Hash bad in badHashes)
        {
            CollectAncestors(repo, bad, reachableFromBads);
        }

        var reachableFromGoods = new HashSet<string>();
        foreach (Hash good in goodHashes)
        {
            CollectAncestors(repo, good, reachableFromGoods);
        }

        var candidates = new List<Hash>();
        var history = repo.GetCommitHistory();

        var graph = repo.BuildCommitGraph();
        foreach (Hash c in history)
        {
            string hex = c.ToHex();
            if (skipHashes.Contains(c)) continue;
            if (badHashes.Contains(c)) continue;
            if (goodHashes.Contains(c)) continue;

            if (reachableFromBads.Contains(hex) && !reachableFromGoods.Contains(hex))
            {
                candidates.Add(c);
            }
        }

        if (candidates.Count == 0)
        {
            Terminal.WriteSuccess("Bisecting: No revisions left to test. The first bad commit is the first bad one marked.");
            return 0;
        }

        if (candidates.Count == 1)
        {
            Hash found = candidates[0];
            Terminal.WriteSuccess($"Bisecting: The first bad commit is:");
            Commit? commit = repo.Objects.ReadCommit(found);
            Terminal.WriteLine($"  {found.Short} {(commit?.Message.Split('\n')[0] ?? "")}");
            return 0;
        }

        Hash midPoint = candidates[candidates.Count / 2];
        Terminal.WriteLine($"Bisecting: {candidates.Count} revisions left to test after this (roughly)");
        Terminal.WriteLine($"[{midPoint.Short}]");

        Commit? midCommit = repo.Objects.ReadCommit(midPoint);
        if (midCommit != null)
        {
            Terminal.WriteLine($"  {midCommit.Message.Split('\n')[0]}");
        }

        try
        {
            repo.CheckoutCommit(midPoint);
            repo.Refs.SetHeadDetached(midPoint);
        }
        catch { }

        return 0;
    }

    private static void CollectAncestors(Repository repo, Hash start, HashSet<string> collected)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<Hash>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            collected.Add(hex);

            Commit? commit = repo.Objects.ReadCommit(current);
            if (commit != null)
            {
                foreach (var p in commit.ParentHashes)
                    queue.Enqueue(p);
            }
        }
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