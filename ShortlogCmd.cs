namespace SourceManager;

/// <summary>
/// `sm shortlog` — summarise history by author.
///
/// Answers "who worked on this and how much" without leaving the tool. --json makes the same
/// summary consumable by a report.
/// </summary>
public static class ShortlogCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool numbered = args.GetBoolOption("numbered");
        bool summaryOnly = args.GetBoolOption("summary");
        string? since = args.GetOption("since");

        // Revisions are positional; with none given, summarise HEAD.
        var starts = new List<Hash>();
        foreach (string positional in args.PositionalArgs)
        {
            if (RevisionResolver.TryResolve(repo, positional, out var rev))
            {
                starts.Add(rev.Commit);
                continue;
            }
            Terminal.WriteError($"fatal: ambiguous argument '{positional}': unknown revision");
            return 1;
        }

        if (starts.Count == 0)
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (head.HasValue && !head.Value.Equals(Hash.Zero)) starts.Add(head.Value);
        }

        var seen = new HashSet<Hash>();
        var ordered = new List<Hash>();
        foreach (Hash start in starts)
        {
            foreach (Hash h in repo.GetCommitHistory(start, int.MaxValue))
            {
                if (seen.Add(h)) ordered.Add(h);
            }
        }

        var authors = new Dictionary<string, (string Name, int Count, List<string> Subjects)>(StringComparer.Ordinal);

        DateTimeOffset cutoff = DateTimeOffset.MinValue;
        bool hasCutoff = since != null && DateSpec.TryParse(since, out cutoff);

        foreach (Hash hash in ordered)
        {
            Commit? commit = repo.Objects.ReadCommit(hash);
            if (commit == null) continue;

            if (hasCutoff && commit.Committer.When < cutoff) continue;

            string key = $"{commit.Author.Name} <{commit.Author.Email}>";
            if (!authors.TryGetValue(key, out var entry))
                entry = (commit.Author.Name, 0, new List<string>());

            entry.Count++;
            entry.Subjects.Add(commit.Message.Split('\n')[0]);
            authors[key] = entry;
        }

        var rows = authors
            .OrderByDescending(a => a.Value.Count)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();

        if (json)
        {
            JsonOut.Write("shortlog", new
            {
                totalCommits = rows.Sum(r => r.Value.Count),
                authors = rows.Select(r => new
                {
                    name = r.Value.Name,
                    email = r.Key[(r.Key.IndexOf('<') + 1)..].TrimEnd('>'),
                    commits = r.Value.Count,
                    subjects = summaryOnly ? null : r.Value.Subjects
                }).ToList()
            });
            return 0;
        }

        foreach (var (key, entry) in rows)
        {
            string label = numbered ? entry.Count.ToString().PadLeft(6) : entry.Name;
            Console.WriteLine($"{label}\t{entry.Name} <{key[(key.IndexOf('<') + 1)..]}");
            if (!summaryOnly)
            {
                foreach (string subject in entry.Subjects)
                    Console.WriteLine($"\t\t{subject}");
            }
            Console.WriteLine();
        }

        return 0;
    }
}
