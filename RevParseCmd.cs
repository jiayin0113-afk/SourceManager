using System.Text;

namespace SourceManager;

/// <summary>
/// `sm rev-parse` — resolve revisions and repository facts to plain text.
///
/// This is the contract scripts use to ask "what does this name mean" without parsing prose.
/// With --json the same answers come back as a document.
/// </summary>
public static class RevParseCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool verify = args.GetBoolOption("verify");
        bool quiet = args.GetBoolOption("quiet");
        bool abbrevRef = args.GetBoolOption("abbrev-ref");
        bool showToplevel = args.GetBoolOption("show-toplevel");
        bool isInside = args.GetBoolOption("is-inside-work-tree");
        int? shortLength = args.GetIntOption("short");

        if (showToplevel)
        {
            if (json) JsonOut.Write("rev-parse", new { topLevel = repo.RootPath });
            else Console.WriteLine(repo.RootPath);
            return 0;
        }

        if (isInside)
        {
            if (json) JsonOut.Write("rev-parse", new { insideWorkTree = true });
            else Console.WriteLine("true");
            return 0;
        }

        var specs = args.PositionalArgs;
        if (specs.Count == 0)
        {
            Terminal.WriteError("fatal: no revision given");
            return 1;
        }

        var results = new List<(string Spec, string? Value, string? Kind, string? Error)>();

        foreach (string spec in specs)
        {
            if (spec == "--show-toplevel")
            {
                results.Add((spec, repo.RootPath, "path", null));
                continue;
            }

            if (abbrevRef)
            {
                string? branch = repo.Refs.GetCurrentBranch();
                if (spec == "HEAD" && branch != null)
                {
                    results.Add((spec, branch, "branch", null));
                    continue;
                }
                // A branch name maps to itself; anything else keeps its resolved id.
                if (repo.Refs.GetBranch(spec).HasValue)
                {
                    results.Add((spec, spec, "branch", null));
                    continue;
                }
            }

            try
            {
                var target = RevisionResolver.Resolve(repo, spec);
                string value = shortLength is > 0 and < 64
                    ? target.Commit.ToHex()[..shortLength.Value]
                    : target.Commit.ToHex();

                string kind = target.IsTag ? "tag" : target.IsBranch ? "commit" : "commit";
                results.Add((spec, value, kind, null));
            }
            catch (RevisionException ex)
            {
                results.Add((spec, null, null, ex.Message));
            }
        }

        bool failed = results.Any(r => r.Error != null);

        if (json)
        {
            JsonOut.Write("rev-parse", new
            {
                ok = !failed,
                results = results.Select(r => new
                {
                    spec = r.Spec,
                    value = r.Value,
                    kind = r.Kind,
                    error = r.Error
                }).ToList()
            });
            return failed ? 1 : 0;
        }

        foreach (var r in results)
        {
            if (r.Value != null)
            {
                Console.WriteLine(r.Value);
            }
            else if (!quiet)
            {
                // --verify silences the diagnostic and only signals through the exit code,
                // which is what a script checking a ref wants.
                Terminal.WriteError($"fatal: {r.Error}");
            }
        }

        return failed ? 1 : 0;
    }
}
