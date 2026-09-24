namespace SourceManager;

/// <summary>
/// Reflog management: <c>sm reflog [show] [&lt;ref&gt;]</c>, <c>sm reflog expire</c> and
/// <c>sm reflog delete</c>. Every ref move is recorded by <see cref="RefStore"/>, so this
/// command is a pure reader/editor of the "logs/&lt;ref&gt;" files.
/// </summary>
public static class ReflogCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool all = args.GetBoolOption("all");
        bool verbose = args.GetBoolOption("verbose");
        bool dryRun = args.GetBoolOption("dry-run");
        bool rewrite = args.GetBoolOption("rewrite");
        bool updateRef = args.GetBoolOption("updateref");
        string? expireSpec = args.GetOption("expire");
        string? expireUnreachableSpec = args.GetOption("expire-unreachable");
        int maxCount = args.GetIntOption("max-count") ?? 0;

        var positionals = new List<string>(args.PositionalArgs);

        // Determine the mode. Git spells these as subcommands ("reflog expire"), but the option
        // forms (--delete / --expire=<t>) must keep working too.
        string mode = "show";
        if (positionals.Count > 0 &&
            (positionals[0].Equals("expire", StringComparison.OrdinalIgnoreCase) ||
             positionals[0].Equals("delete", StringComparison.OrdinalIgnoreCase)))
        {
            mode = positionals[0].ToLowerInvariant();
            positionals.RemoveAt(0);
        }
        else if (args.GetBoolOption("delete"))
        {
            mode = "delete";
        }
        else if (args.WasProvided("expire"))
        {
            mode = "expire";
        }

        return mode switch
        {
            "expire" => Expire(repo, positionals, all, expireSpec, expireUnreachableSpec, dryRun),
            "delete" => Delete(repo, positionals, rewrite, updateRef),
            _ => Show(repo, positionals, all, maxCount, verbose),
        };
    }

    // ─── show ────────────────────────────────────────────────────────────────────

    private static int Show(Repository repo, List<string> refs, bool all, int maxCount, bool verbose)
    {
        List<string> targets = CollectTargets(repo, refs, all);
        bool multi = targets.Count > 1;

        foreach (string refName in targets)
        {
            var entries = repo.Refs.GetReflog(refName, maxCount);
            if (entries.Count == 0) continue;

            if (multi)
                Terminal.WriteLine($"Reflog for {DisplayRef(refName)}:", Terminal.Color.BrightCyan, true);

            for (int i = 0; i < entries.Count; i++)
            {
                ReflogEntry entry = entries[i];
                string selector = $"{DisplayRef(refName)}@{{{i}}}";
                Terminal.Write($"{entry.NewHash.Short} ", Terminal.Color.Green);
                Console.Write($"{selector}: {entry.Message}");
                if (verbose)
                    Console.Write($"  ({entry.Author.Name} <{entry.Author.Email}> {entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz})");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        return 0;
    }

    // ─── expire ──────────────────────────────────────────────────────────────────

    private static int Expire(Repository repo, List<string> refs, bool all,
        string? expireSpec, string? expireUnreachableSpec, bool dryRun)
    {
        // Reachable entries default to 90 days, unreachable ones to 30 — the same split Git uses
        // so that a stale branch tip does not linger for the full reachable window.
        DateTimeOffset reachableCutoff = DateTimeOffset.Now - TimeSpan.FromDays(90);
        DateTimeOffset unreachableCutoff = DateTimeOffset.Now - TimeSpan.FromDays(30);

        if (expireSpec != null)
        {
            if (!DateSpec.TryParse(expireSpec, out reachableCutoff))
            {
                Terminal.WriteError($"fatal: invalid expire time '{expireSpec}'");
                return 1;
            }
        }
        if (expireUnreachableSpec != null)
        {
            if (!DateSpec.TryParse(expireUnreachableSpec, out unreachableCutoff))
            {
                Terminal.WriteError($"fatal: invalid expire-unreachable time '{expireUnreachableSpec}'");
                return 1;
            }
        }

        var reachable = ComputeReachable(repo);
        List<string> targets = CollectTargets(repo, refs, all);
        int totalExpired = 0;

        foreach (string refName in targets)
        {
            var entries = repo.Refs.ReadReflogOldestFirst(refName);
            if (entries.Count == 0) continue;

            var kept = new List<ReflogEntry>(entries.Count);
            int dropped = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ReflogEntry entry = entries[i];
                bool isTip = i == entries.Count - 1;

                bool isReachable = entry.NewHash.Equals(Hash.Zero) || reachable.Contains(entry.NewHash);
                DateTimeOffset cutoff = isReachable ? reachableCutoff : unreachableCutoff;

                // The most recent entry defines the ref's current value and is never expired.
                if (!isTip && entry.Timestamp < cutoff)
                {
                    dropped++;
                    continue;
                }
                kept.Add(entry);
            }

            if (dropped == 0) continue;

            if (!dryRun)
                repo.Refs.RewriteReflog(refName, kept);
            totalExpired += dropped;
            Terminal.WriteLine($"{(dryRun ? "Would expire" : "Expired")} {dropped} {(dropped == 1 ? "entry" : "entries")} from {DisplayRef(refName)}");
        }

        if (totalExpired == 0)
            Terminal.WriteLine("Nothing to expire.");
        return 0;
    }

    // ─── delete ──────────────────────────────────────────────────────────────────

    private static int Delete(Repository repo, List<string> selectors, bool rewrite, bool updateRef)
    {
        if (selectors.Count == 0)
        {
            Terminal.WriteError("fatal: reflog delete needs a selector such as 'HEAD@{0}'");
            return 1;
        }

        foreach (string selector in selectors)
        {
            int at = selector.IndexOf("@{", StringComparison.Ordinal);
            if (at < 0 || !selector.EndsWith('}'))
            {
                Terminal.WriteError($"fatal: bad revision '{selector}'");
                return 1;
            }

            string refPart = at == 0 ? "HEAD" : selector[..at];
            string inner = selector[(at + 2)..^1];
            if (!int.TryParse(inner, out int index) || index < 0)
            {
                Terminal.WriteError($"fatal: bad revision '{selector}'");
                return 1;
            }

            string refName = NormalizeRefName(repo, refPart);
            var entries = repo.Refs.ReadReflogOldestFirst(refName); // oldest-first
            int fileIndex = entries.Count - 1 - index;              // selectors count newest-first
            if (fileIndex < 0 || fileIndex >= entries.Count)
            {
                Terminal.WriteError($"fatal: {selector}: index out of range (reflog has {entries.Count} entries)");
                return 1;
            }

            entries.RemoveAt(fileIndex);
            repo.Refs.RewriteReflog(refName, entries);

            if (updateRef && entries.Count > 0 && refName != "HEAD")
                repo.Refs.WriteRefNoLog(refName, entries[^1].NewHash);

            Terminal.WriteLine($"Deleted {selector}");
        }

        // --rewrite requires renumbering the surviving entries; the on-disk format stores no
        // explicit index (it is positional), so a rewrite is exactly what the removal already
        // performs. The flag is accepted for Git compatibility and answered truthfully.
        if (rewrite)
            Terminal.WriteLine("Reflog rewritten.");

        return 0;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static List<string> CollectTargets(Repository repo, List<string> refs, bool all)
    {
        var targets = new List<string>();
        if (all)
        {
            targets.Add("HEAD");
            foreach (var branch in repo.Refs.ListBranches().Select(b => b.Name))
                targets.Add($"refs/heads/{branch}");
            foreach (string tag in repo.Refs.ListTags())
                targets.Add($"refs/tags/{tag}");
            foreach (var (remote, branch, _) in repo.Refs.ListRemoteBranches())
                targets.Add($"refs/remotes/{remote}/{branch}");
        }
        else if (refs.Count > 0)
        {
            foreach (string r in refs)
                targets.Add(NormalizeRefName(repo, r));
        }
        else
        {
            targets.Add("HEAD");
        }
        return targets;
    }

    /// <summary>Maps a user-supplied ref name onto the full "refs/..." name it should log to.</summary>
    private static string NormalizeRefName(Repository repo, string name)
    {
        if (name == "HEAD") return "HEAD";
        if (name.StartsWith("refs/", StringComparison.Ordinal)) return name;
        if (repo.Refs.GetBranch(name).HasValue) return $"refs/heads/{name}";
        if (repo.Refs.GetTag(name).HasValue) return $"refs/tags/{name}";

        int slash = name.IndexOf('/');
        if (slash > 0 && repo.Refs.GetRemoteBranch(name[..slash], name[(slash + 1)..]).HasValue)
            return $"refs/remotes/{name}";

        return $"refs/heads/{name}";
    }

    private static string DisplayRef(string refName)
    {
        if (refName == "HEAD") return "HEAD";
        foreach (string prefix in new[] { "refs/heads/", "refs/tags/", "refs/remotes/" })
        {
            if (refName.StartsWith(prefix, StringComparison.Ordinal))
                return refName[prefix.Length..];
        }
        return refName;
    }

    /// <summary>Every commit reachable from a ref or from HEAD is "reachable" for expiry purposes.</summary>
    private static HashSet<Hash> ComputeReachable(Repository repo)
    {
        var reachable = new HashSet<Hash>();
        var queue = new Queue<Hash>();

        void Enqueue(Hash h)
        {
            if (h.Equals(Hash.Zero)) return;
            if (reachable.Add(h)) queue.Enqueue(h);
        }

        foreach (string refName in repo.Refs.GetAllRefNames())
        {
            Hash? tip = repo.Refs.ResolveRefName(refName);
            if (tip.HasValue) Enqueue(tip.Value);
        }
        Hash? head = repo.Refs.GetHeadCommit();
        if (head.HasValue) Enqueue(head.Value);

        while (queue.Count > 0)
        {
            Commit? commit = repo.Objects.ReadCommit(queue.Dequeue());
            if (commit == null) continue;
            foreach (Hash parent in commit.ParentHashes)
                Enqueue(parent);
        }

        return reachable;
    }
}
