namespace SourceManager;

public static class LogCmd
{
    /// <summary>Prints reflog entries in "&lt;hash&gt; &lt;ref&gt;@{n}: &lt;message&gt;" form.</summary>
    private static int ShowReflog(Repository repo, ParseResult args, int maxCount)
    {
        var targets = new List<string> { "HEAD" };
        if (args.GetBoolOption("all"))
        {
            foreach (string branch in repo.Refs.ListBranches().Select(b => b.Name))
                targets.Add($"refs/heads/{branch}");
        }

        foreach (string refName in targets)
        {
            var entries = repo.Refs.GetReflog(refName, maxCount);
            if (entries.Count == 0) continue;

            string display = refName == "HEAD" ? "HEAD" : refName["refs/heads/".Length..];
            for (int i = 0; i < entries.Count; i++)
            {
                ReflogEntry entry = entries[i];
                Terminal.Write($"{entry.NewHash.Short} ", Terminal.Color.Green);
                Console.WriteLine($"{display}@{{{i}}}: {entry.Message}");
            }
        }
        return 0;
    }

    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        int maxCount = args.GetIntOption("max-count") ?? 100;
        int skip = args.GetIntOption("skip") ?? 0;
        bool oneline = args.GetBoolOption("oneline");
        bool graph = args.GetBoolOption("graph");
        string? decorate = args.GetOption("decorate");
        bool showAll = args.GetBoolOption("all");
        string? format = args.GetOption("format");
        string? author = args.GetOption("author");
        string? committer = args.GetOption("committer");
        string? grep = args.GetOption("grep");
        string? since = args.GetOption("since");
        string? until = args.GetOption("until");
        bool firstParent = args.GetBoolOption("first-parent");
        bool noMerges = args.GetBoolOption("no-merges");
        bool mergesOnly = args.GetBoolOption("merges");
        bool follow = args.GetBoolOption("follow");
        bool reverse = args.GetBoolOption("reverse");
        bool patch = args.GetBoolOption("patch");
        bool stat = args.GetBoolOption("stat");
        bool shortstat = args.GetBoolOption("shortstat");
        string? diffFilter = args.GetOption("diff-filter");
        string? branches = args.GetOption("branches");
        string? tagsOption = args.GetOption("tags");

        // `log -g` / `log --reflog` walks the reflog instead of the commit graph.
        if (args.GetBoolOption("reflog"))
            return ShowReflog(repo, args, maxCount);

        // Positional arguments are revisions, or a single pathspec when they name no revision.
        // Previously every positional was treated as a path, so `sm log feature/x` silently
        // returned nothing instead of walking that branch.
        var starts = new List<Hash>();
        string? pathFilter = null;
        bool pathFilterIsPrefix = false;

        foreach (string positional in args.PositionalArgs)
        {
            if (RevisionResolver.TryResolve(repo, positional, out var rev))
            {
                starts.Add(rev.Commit);
                continue;
            }

            if (pathFilter == null && args.PositionalArgs.Count == 1)
            {
                pathFilter = positional;
                pathFilterIsPrefix = true;
                continue;
            }

            Terminal.WriteError($"fatal: ambiguous argument '{positional}': unknown revision or path not in the working tree");
            return 1;
        }

        if (starts.Count == 0)
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (head.HasValue && !head.Value.Equals(Hash.Zero))
                starts.Add(head.Value);
        }

        // Preserve first-seen order across multiple start points.
        var ordered = new List<Hash>();
        var seen = new HashSet<Hash>();
        int perStart = maxCount + skip;
        foreach (Hash start in starts)
        {
            foreach (Hash h in repo.GetCommitHistory(start, perStart, pathFilter, pathFilterIsPrefix))
            {
                if (seen.Add(h)) ordered.Add(h);
            }
        }
        var history = ordered;

        // --follow tracks a single path backwards across renames, so history is not truncated
        // at the point the file was moved.
        if (follow && pathFilter != null)
        {
            var followed = FollowPath(repo, starts, pathFilter, maxCount + skip);
            history = followed;
        }

        if (reverse)
            history.Reverse();

        var refNames = ResolveRefDecorations(repo);

        int count = 0;
        var jsonCommits = JsonOut.Requested(args) ? new List<object>() : null;

        foreach (Hash commitHash in history)
        {
            if (skip > 0 && count < skip) { count++; continue; }
            if (count >= maxCount) break;

            Commit? commit = repo.Objects.ReadCommit(commitHash);
            if (commit == null) continue;

            if (author != null)
            {
                if (!commit.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase) &&
                    !commit.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (committer != null)
            {
                if (!commit.Committer.Name.Contains(committer, StringComparison.OrdinalIgnoreCase) &&
                    !commit.Committer.Email.Contains(committer, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (grep != null)
            {
                if (!commit.Message.Contains(grep, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (since != null && !MatchesDateBound(commit.Committer.When, since, isLowerBound: true))
                continue;
            if (until != null && !MatchesDateBound(commit.Committer.When, until, isLowerBound: false))
                continue;

            if (noMerges && commit.ParentHashes.Count > 1) continue;
            if (mergesOnly && commit.ParentHashes.Count <= 1) continue;

            if (diffFilter != null)
            {
                if (diffFilter == "A" && commit.ParentHashes.Count > 0) continue;
                if (diffFilter == "M" && commit.ParentHashes.Count == 0) continue;
            }

            if (jsonCommits != null)
            {
                jsonCommits.Add(new
                {
                    id = commitHash.ToHex(),
                    short_id = commitHash.Short,
                    tree = JsonOut.Hex(commit.TreeHash),
                    parents = commit.ParentHashes.Select(p => p.ToHex()).ToList(),
                    author = new
                    {
                        name = commit.Author.Name,
                        email = commit.Author.Email,
                        // Emitted as an ISO-8601 string so consumers do not have to depend on
                        // how the JSON serializer happens to render a DateTimeOffset.
                        date = JsonOut.Timestamp(commit.Author.When),
                        unix = commit.Author.When.ToUnixTimeSeconds(),
                        tz = commit.Author.TimezoneOffset
                    },
                    committer = new
                    {
                        name = commit.Committer.Name,
                        email = commit.Committer.Email,
                        date = JsonOut.Timestamp(commit.Committer.When),
                        unix = commit.Committer.When.ToUnixTimeSeconds(),
                        tz = commit.Committer.TimezoneOffset
                    },
                    subject = commit.Message.Split('\n')[0],
                    body = commit.Message,
                    refs = refNames.TryGetValue(commitHash.ToHex(), out var names) ? names : new List<string>()
                });
            }
            else
            {
                PrintCommit(repo, commit, commitHash, refNames, oneline, graph, format, decorate, patch, stat, shortstat);
            }

            count++;
        }

        if (jsonCommits != null)
        {
            JsonOut.Write("log", new
            {
                count = jsonCommits.Count,
                commits = jsonCommits
            });
        }

        return 0;
    }

    /// <summary>
    /// Walks history from <paramref name="starts"/> newest-first, following a single file across
    /// renames. At each commit whose tree lacks the current path, the commit is diffed against
    /// its first parent and a rename is followed to the old path.
    /// </summary>
    private static List<Hash> FollowPath(Repository repo, List<Hash> starts, string initialPath, int maxCount)
    {
        var result = new List<Hash>();
        var visited = new HashSet<Hash>();
        var queue = new Queue<(Hash Commit, string Path)>();
        var startSet = starts.Count > 0
            ? starts
            : (repo.Refs.GetHeadCommit() is { } h && !h.Equals(Hash.Zero) ? new List<Hash> { h } : new List<Hash>());

        foreach (Hash s in startSet)
            queue.Enqueue((s, initialPath));

        while (queue.Count > 0 && result.Count < maxCount)
        {
            var (commitHash, path) = queue.Dequeue();
            if (!visited.Add(commitHash)) continue;

            Commit? commit = repo.Objects.ReadCommit(commitHash);
            if (commit?.TreeHash.HasValue != true) continue;

            var files = repo.GetTreeEntries(commit.TreeHash.Value);

            if (files.ContainsKey(path))
            {
                result.Add(commitHash);
                foreach (Hash p in commit.ParentHashes)
                    queue.Enqueue((p, path));
                continue;
            }

            // The path is gone in this commit: follow the rename that removed it.
            if (commit.ParentHashes.Count > 0)
            {
                var renameDiffs = repo.Diff.DiffCommits(commit.ParentHashes[0], commitHash);
                var rename = renameDiffs.FirstOrDefault(d =>
                    d.Kind == ChangeKind.Renamed && d.NewPath == path);

                if (rename != null)
                {
                    result.Add(commitHash);
                    foreach (Hash p in commit.ParentHashes)
                        queue.Enqueue((p, rename.OldPath));
                    continue;
                }
            }

            // Not a rename at this commit; keep walking so older edits are still found.
            foreach (Hash p in commit.ParentHashes)
                queue.Enqueue((p, path));
        }

        return result;
    }

    /// <summary>
    /// Date filtering for --since/--until. Accepts an ISO date or a relative form such as
    /// "2.weeks.ago"; an unparseable value never silently drops commits.
    /// </summary>
    private static bool MatchesDateBound(DateTimeOffset when, string spec, bool isLowerBound)
    {
        if (!DateSpec.TryParse(spec, out DateTimeOffset bound))
            return true;

        return isLowerBound ? when >= bound : when <= bound;
    }

    private static Dictionary<string, List<string>> ResolveRefDecorations(Repository repo)
    {
        var decorations = new Dictionary<string, List<string>>();
        string? headBranch = repo.Refs.GetCurrentBranch();

        foreach (var branch in repo.Refs.ListBranches())
        {
            string hashKey = branch.TipHash.ToHex();
            if (!decorations.ContainsKey(hashKey))
                decorations[hashKey] = new List<string>();

            if (branch.IsHead)
                decorations[hashKey].Add($"HEAD -> {branch.Name}");
            else
                decorations[hashKey].Add(branch.Name);
        }

        foreach (string tag in repo.Refs.ListTags())
        {
            Hash? tagHash = repo.Refs.GetTag(tag);
            if (tagHash.HasValue)
            {
                string hashKey = tagHash.Value.ToHex();
                if (!decorations.ContainsKey(hashKey))
                    decorations[hashKey] = new List<string>();
                decorations[hashKey].Add($"tag: {tag}");
            }
        }

        if (headBranch == null)
        {
            Hash? headCommit = repo.Refs.GetHeadCommit();
            if (headCommit.HasValue)
            {
                string hashKey = headCommit.Value.ToHex();
                if (!decorations.ContainsKey(hashKey))
                    decorations[hashKey] = new List<string>();
                decorations[hashKey].Add("HEAD");
            }
        }

        return decorations;
    }

    private static void PrintCommit(Repository repo, Commit commit, Hash commitHash,
        Dictionary<string, List<string>> decorations, bool oneline, bool graph, string? format,
        string? decorate, bool patch, bool stat, bool shortstat)
    {
        string shortHash = commitHash.Short;
        string decoration = FormatDecorations(commitHash, decorations, decorate);

        if (oneline)
        {
            Terminal.Write($"{shortHash} ", Terminal.Color.Yellow);
            if (!string.IsNullOrEmpty(decoration))
                Terminal.Write($"{decoration} ", Terminal.Color.Cyan);
            Console.WriteLine(commit.Message.Split('\n')[0]);
            return;
        }

        if (format != null)
        {
            string output = FormatCommitString(format, commit, commitHash, decoration);
            Console.Write(output);
            return;
        }

        Terminal.Write($"commit {commitHash.ToHex()}", Terminal.Color.Yellow, true);
        if (!string.IsNullOrEmpty(decoration))
            Terminal.Write($" ({decoration})", Terminal.Color.Cyan);
        Console.WriteLine();

        if (commit.ParentHashes.Count > 1)
        {
            Console.Write("Merge:");
            foreach (var p in commit.ParentHashes)
                Console.Write($" {p.Short}");
            Console.WriteLine();
        }

        Console.WriteLine($"Author:    {commit.Author.Name} <{commit.Author.Email}>");
        Console.WriteLine($"Date:      {commit.Author.When:ddd MMM dd HH:mm:ss yyyy}");

        Console.WriteLine();
        foreach (string line in commit.Message.Split('\n'))
            Console.WriteLine($"    {line}");

        if (stat)
        {
            PrintStat(repo, commit, commitHash);
        }

        if (shortstat)
        {
            PrintShortStat(repo, commit, commitHash);
        }

        if (patch)
        {
            PrintPatch(repo, commitHash);
        }

        Console.WriteLine();
    }

    private static string FormatDecorations(Hash hash, Dictionary<string, List<string>> decorations, string? decorate)
    {
        if (decorate == "no") return "";
        string key = hash.ToHex();
        if (decorations.TryGetValue(key, out var names))
        {
            return string.Join(", ", names);
        }
        return "";
    }

    private static string FormatCommitString(string format, Commit commit, Hash commitHash, string decoration)
    {
        string result = format;
        result = result.Replace("%H", commitHash.ToHex());
        result = result.Replace("%h", commitHash.Short);
        result = result.Replace("%s", commit.Message.Split('\n')[0]);
        result = result.Replace("%an", commit.Author.Name);
        result = result.Replace("%ae", commit.Author.Email);
        result = result.Replace("%cn", commit.Committer.Name);
        result = result.Replace("%ce", commit.Committer.Email);
        result = result.Replace("%d", string.IsNullOrEmpty(decoration) ? "" : $" ({decoration})");
        result = result.Replace("\\n", "\n");
        result = result.Replace("\\t", "\t");
        return result;
    }

    private static void PrintStat(Repository repo, Commit commit, Hash commitHash)
    {
        if (commit.ParentHashes.Count == 0)
        {
            if (!commit.TreeHash.HasValue) return;
            var entries = repo.GetTreeEntries(commit.TreeHash.Value);
            Console.WriteLine($" {entries.Count} files changed");
            foreach (var (path, _) in entries)
                Console.WriteLine($" create mode 100644 {path}");
        }
        else
        {
            var diffs = repo.Diff.DiffCommits(commit.ParentHashes[0], commitHash);
            int added = 0, deleted = 0;
            foreach (var diff in diffs)
            {
                foreach (var hunk in diff.Hunks)
                {
                    foreach (var line in hunk.Lines)
                    {
                        if (line.Type == DiffLineType.Added) added++;
                        else if (line.Type == DiffLineType.Deleted) deleted++;
                    }
                }
            }
            Console.WriteLine($" {diffs.Count} files changed, {added} insertions(+), {deleted} deletions(-)");
        }
    }

    private static void PrintShortStat(Repository repo, Commit commit, Hash commitHash)
    {
        if (commit.ParentHashes.Count == 0)
        {
            if (!commit.TreeHash.HasValue) return;
            var entries = repo.GetTreeEntries(commit.TreeHash.Value);
            Console.WriteLine($" {entries.Count} files changed");
        }
        else
        {
            var diffs = repo.Diff.DiffCommits(commit.ParentHashes[0], commitHash);
            Console.WriteLine($" {diffs.Count} files changed");
        }
    }

    private static void PrintPatch(Repository repo, Hash commitHash)
    {
        Commit? commit = repo.Objects.ReadCommit(commitHash);
        if (commit == null) return;
        if (commit.ParentHashes.Count == 0) return;

        var diffs = repo.Diff.DiffCommits(commit.ParentHashes[0], commitHash);
        foreach (var diff in diffs)
        {
            string oldPath = diff.OldPath;
            string newPath = diff.NewPath;
            Console.WriteLine($"diff --git a/{oldPath} b/{newPath}");

            if (diff.Kind == ChangeKind.Deleted)
                Console.WriteLine($"deleted file mode {(int)diff.OldMode:D6}");
            else if (diff.Kind == ChangeKind.Added)
                Console.WriteLine($"new file mode {(int)diff.NewMode:D6}");
            else
                Console.WriteLine($"index {diff.OldHash?.Short ?? "0000000"}..{diff.NewHash?.Short ?? "0000000"} {(int)diff.OldMode:D6}");

            Console.WriteLine($"--- a/{oldPath}");
            Console.WriteLine($"+++ b/{newPath}");

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
                    string color = line.Type switch
                    {
                        DiffLineType.Added => "\u001b[32m",
                        DiffLineType.Deleted => "\u001b[31m",
                        _ => ""
                    };
                    string reset = line.Type != DiffLineType.Context ? "\u001b[0m" : "";
                    Console.WriteLine($"{color}{prefix}{line.Text}{reset}");
                }
            }
        }
    }
}