namespace SourceManager;

/// <summary>
/// `sm merge-base` — find the common ancestor of two commits.
///
/// With --is-ancestor it becomes a predicate instead of a query, which is the form scripts
/// actually need ("is this branch already contained in that one").
/// </summary>
public static class MergeBaseCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool isAncestor = args.GetBoolOption("is-ancestor");
        bool all = args.GetBoolOption("all");

        var specs = args.PositionalArgs;
        if (specs.Count < 2)
        {
            Terminal.WriteError("error: usage: sm merge-base [--is-ancestor] <commit> <commit>");
            return 1;
        }

        var resolved = new List<Hash>();
        foreach (string spec in specs)
        {
            try
            {
                resolved.Add(RevisionResolver.Resolve(repo, spec).Commit);
            }
            catch (RevisionException ex)
            {
                Terminal.WriteError($"fatal: {ex.Message}");
                return 1;
            }
        }

        if (isAncestor)
        {
            if (resolved.Count != 2)
            {
                Terminal.WriteError("error: --is-ancestor takes exactly two commits");
                return 1;
            }

            // Exit code is the answer: 0 when the first is an ancestor of the second, 1 when not.
            bool result = repo.IsAncestor(resolved[0], resolved[1]);
            if (json)
                JsonOut.Write("merge-base", new { isAncestor = result, ancestor = resolved[0].ToHex(), descendant = resolved[1].ToHex() });
            else
                Console.WriteLine(result ? "true" : "false");
            return result ? 0 : 1;
        }

        // Fold pairwise: the common ancestor of N commits is the common ancestor of the running
        // result and the next commit.
        Hash? current = resolved[0];
        for (int i = 1; i < resolved.Count && current.HasValue; i++)
            current = repo.FindMergeBase(current.Value, resolved[i]);

        if (!current.HasValue || current.Value.Equals(Hash.Zero))
        {
            if (json) JsonOut.Write("merge-base", new { found = false });
            else Terminal.WriteLine(""); // no common ancestor; empty output, non-error (git emits nothing)
            return 0;
        }

        if (json)
            JsonOut.Write("merge-base", new { found = true, id = current.Value.ToHex(), shortId = current.Value.Short });
        else
            Console.WriteLine(all ? current.Value.ToHex() : current.Value.ToHex());

        return 0;
    }
}
