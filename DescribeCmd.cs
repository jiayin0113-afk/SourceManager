using System.Text;

namespace SourceManager;

/// <summary>
/// `sm describe` — name a commit by the nearest tag, the way release tooling expects.
///
/// Produces "v1.2.0" when the commit IS a tag, or "v1.2.0-4-g1a2b3c4" when it is four commits
/// past one. `--json` exposes the same parts separately so a tool never has to re-parse it.
/// </summary>
public static class DescribeCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool tagsOnly = args.GetBoolOption("tags");
        bool always = args.GetBoolOption("always");
        int? abbrev = args.GetIntOption("abbrev") ?? 7;

        string spec = args.GetPositional(0) ?? "HEAD";

        Hash commit;
        try
        {
            commit = RevisionResolver.Resolve(repo, spec).Commit;
        }
        catch (RevisionException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        // Map every tag to the commit it points at (peeling annotated tags).
        var tagTargets = new Dictionary<Hash, List<string>>();
        foreach (string tag in repo.Refs.ListTags())
        {
            Hash? tagHash = repo.Refs.GetTag(tag);
            if (!tagHash.HasValue) continue;

            Hash peeled = RevisionResolver.PeelToCommit(repo, tagHash.Value);
            if (!tagTargets.TryGetValue(peeled, out var names))
            {
                names = new List<string>();
                tagTargets[peeled] = names;
            }
            names.Add(tag);
        }

        // Walk back from the commit looking for the closest tagged ancestor, preferring the
        // fewest hops; ties are broken by tag name so the answer is stable.
        var visited = new HashSet<Hash>();
        var queue = new Queue<(Hash Commit, int Distance)>();
        queue.Enqueue((commit, 0));

        Hash? best = null;
        int bestDistance = int.MaxValue;
        string? bestTag = null;

        while (queue.Count > 0)
        {
            var (current, distance) = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (distance > bestDistance) continue;

            if (tagTargets.TryGetValue(current, out var names))
            {
                string candidate = names
                    .Where(n => !tagsOnly || true)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .First();
                if (distance < bestDistance ||
                    (distance == bestDistance && bestTag != null &&
                     string.CompareOrdinal(candidate, bestTag) < 0))
                {
                    best = current;
                    bestDistance = distance;
                    bestTag = candidate;
                }
                // A tag on this commit is the closest possible; no need to look further along it.
                continue;
            }

            Commit? c = repo.Objects.ReadCommit(current);
            if (c == null) continue;
            foreach (Hash parent in c.ParentHashes)
                queue.Enqueue((parent, distance + 1));
        }

        if (best == null || bestTag == null)
        {
            if (!always)
            {
                Terminal.WriteError($"fatal: no tags can describe '{commit.Short}'");
                return 1;
            }

            string fallback = commit.ToHex()[..Math.Clamp(abbrev.Value, 4, 64)];
            if (json)
                JsonOut.Write("describe", new { described = fallback, commit = commit.ToHex(), tag = (string?)null, distance = 0 });
            else
                Console.WriteLine(fallback);
            return 0;
        }

        bool exact = bestDistance == 0;
        string description = exact
            ? bestTag
            : $"{bestTag}-{bestDistance}-g{commit.ToHex()[..Math.Clamp(abbrev.Value, 4, 64)]}";

        if (json)
        {
            JsonOut.Write("describe", new
            {
                described = description,
                commit = commit.ToHex(),
                tag = bestTag,
                tagCommit = best!.Value.ToHex(),
                distance = bestDistance,
                exact
            });
        }
        else
        {
            Console.WriteLine(description);
        }

        return 0;
    }
}
