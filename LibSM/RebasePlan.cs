using System.Text;
using System.Text.Json;

namespace SourceManager;

/// <summary>One step of an interactive rebase.</summary>
public enum RebaseOp
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop,
    Exec
}

public sealed record RebaseStep(RebaseOp Op, Hash Commit, string Subject)
{
    public string OpName => Op switch
    {
        RebaseOp.Pick => "pick",
        RebaseOp.Reword => "reword",
        RebaseOp.Edit => "edit",
        RebaseOp.Squash => "squash",
        RebaseOp.Fixup => "fixup",
        RebaseOp.Drop => "drop",
        RebaseOp.Exec => "exec",
        _ => "pick"
    };

    public static bool TryParseOp(string text, out RebaseOp op)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "p":
            case "pick": op = RebaseOp.Pick; return true;
            case "r":
            case "reword": op = RebaseOp.Reword; return true;
            case "e":
            case "edit": op = RebaseOp.Edit; return true;
            case "s":
            case "squash": op = RebaseOp.Squash; return true;
            case "f":
            case "fixup": op = RebaseOp.Fixup; return true;
            case "d":
            case "drop": op = RebaseOp.Drop; return true;
            case "x":
            case "exec": op = RebaseOp.Exec; return true;
            default: op = RebaseOp.Pick; return false;
        }
    }
}

/// <summary>
/// The ordered plan for an interactive rebase, and its round trip through a text editor.
///
/// The plan is a real artifact, not a command-line convenience: it is written to disk, edited,
/// re-read, and then executed one step at a time with the position persisted, which is what makes
/// a rebase stoppable and resumable.
/// </summary>
public static class RebasePlan
{
    private const string PlanFile = "REBASE_PLAN";

    public static string PathFor(Repository repo) => Path.Combine(repo.SmPath, PlanFile);

    public static bool Exists(Repository repo) => File.Exists(PathFor(repo));

    public static void Save(Repository repo, IReadOnlyList<RebaseStep> steps)
    {
        var payload = new
        {
            steps = steps.Select(s => new
            {
                op = s.OpName,
                commit = s.Commit.ToHex(),
                subject = s.Subject
            }).ToList()
        };
        File.WriteAllText(PathFor(repo), JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<RebaseStep> Load(Repository repo)
    {
        var steps = new List<RebaseStep>();
        if (!File.Exists(PathFor(repo))) return steps;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(PathFor(repo)));
            if (!doc.RootElement.TryGetProperty("steps", out var arr)) return steps;

            foreach (var elem in arr.EnumerateArray())
            {
                string opName = elem.GetProperty("op").GetString() ?? "pick";
                string commitHex = elem.GetProperty("commit").GetString() ?? "";
                string subject = elem.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";

                if (commitHex.Length != 64 || !RebaseStep.TryParseOp(opName, out RebaseOp op)) continue;
                steps.Add(new RebaseStep(op, Hash.Parse(commitHex), subject));
            }
        }
        catch
        {
            // A malformed plan must not be silently treated as an empty one.
            throw new InvalidOperationException("the rebase plan is unreadable; run 'sm rebase --abort'");
        }

        return steps;
    }

    public static void Clear(Repository repo)
    {
        string p = PathFor(repo);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Renders the plan as an editable todo list.</summary>
    public static string Render(IReadOnlyList<RebaseStep> steps)
    {
        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append("# Rebase plan. Each line is one step, applied from top to bottom.\n");
        sb.Append("#\n");
        sb.Append("#   pick   <commit>  keep the commit\n");
        sb.Append("#   reword <commit>  keep the commit, but edit its message\n");
        sb.Append("#   edit   <commit>  stop after applying so you can amend it\n");
        sb.Append("#   squash <commit>  meld into the previous commit, combining messages\n");
        sb.Append("#   fixup  <commit>  meld into the previous commit, discarding its message\n");
        sb.Append("#   drop   <commit>  remove the commit\n");
        sb.Append("#\n");
        sb.Append("# Reordering the lines reorders the commits. Deleting a line drops that commit.\n");
        sb.Append('\n');

        foreach (var step in steps)
        {
            sb.Append($"{step.OpName,-6} {step.Commit.Short} {step.Subject}\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses an edited todo list. Unknown verbs and unparsable commits are reported rather than
    /// skipped, because silently dropping a step loses the user's work.
    /// </summary>
    public static bool TryParse(string text, IReadOnlyList<RebaseStep> original,
        out List<RebaseStep> steps, out string? error)
    {
        steps = new List<RebaseStep>();
        error = null;

        // Abbreviated ids are matched against the original steps, so the editor may show and
        // keep short ids without breaking the mapping.
        var byShort = new Dictionary<string, RebaseStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in original)
        {
            string shortId = step.Commit.Short;
            if (!byShort.ContainsKey(shortId)) byShort[shortId] = step;
        }

        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int space = line.IndexOf(' ');
            if (space <= 0)
            {
                error = $"cannot parse rebase line: '{line}'";
                return false;
            }

            string verb = line[..space];
            if (!RebaseStep.TryParseOp(verb, out RebaseOp op))
            {
                error = $"unknown rebase command '{verb}'";
                return false;
            }

            string rest = line[(space + 1)..].Trim();
            int subjectSpace = rest.IndexOf(' ');
            string id = subjectSpace < 0 ? rest : rest[..subjectSpace];

            if (!byShort.TryGetValue(id, out RebaseStep? original_step) &&
                !byShort.Values.Any(s => s.Commit.ToHex().StartsWith(id, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{id}' does not name a commit in this rebase";
                return false;
            }

            RebaseStep step = original_step
                ?? byShort.Values.First(s => s.Commit.ToHex().StartsWith(id, StringComparison.OrdinalIgnoreCase));

            steps.Add(step with { Op = op });
        }

        if (steps.Count == 0)
        {
            error = "the rebase plan is empty; use 'sm rebase --abort' to give up";
            return false;
        }

        // Every commit must appear at most once: a duplicate would apply the same change twice.
        var seen = new HashSet<Hash>();
        foreach (var step in steps)
        {
            if (!seen.Add(step.Commit))
            {
                error = $"commit {step.Commit.Short} appears more than once in the plan";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rearranges a plan for --autosquash: a "fixup!"/"squash!" commit is moved directly after
    /// the commit its message names, and marked fixup/squash accordingly.
    /// </summary>
    public static List<RebaseStep> Autosquash(IReadOnlyList<RebaseStep> steps)
    {
        var result = new List<RebaseStep>(steps);
        var consumed = new HashSet<Hash>();

        for (int i = 0; i < result.Count; i++)
        {
            var step = result[i];
            if (step.Op is RebaseOp.Fixup or RebaseOp.Squash) continue;

            RebaseOp? implied = null;
            string subject = step.Subject;
            if (subject.StartsWith("fixup! ", StringComparison.OrdinalIgnoreCase)) implied = RebaseOp.Fixup;
            else if (subject.StartsWith("squash! ", StringComparison.OrdinalIgnoreCase)) implied = RebaseOp.Squash;
            if (implied == null) continue;

            string target = subject[(subject.IndexOf('!') + 1)..].Trim();
            if (target.Length == 0) continue;

            int targetIndex = -1;
            for (int j = 0; j < result.Count; j++)
            {
                if (j == i || consumed.Contains(result[j].Commit)) continue;
                if (result[j].Subject.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = j;
                    break;
                }
            }

            if (targetIndex < 0) continue;

            consumed.Add(step.Commit);
            result.RemoveAt(i);
            if (targetIndex > i) targetIndex--;
            result.Insert(targetIndex + 1, step with { Op = implied.Value });
            i = -1; // restart: an insertion can enable another match
        }

        return result;
    }

    /// <summary>Opens the plan in the user's editor and re-reads it.</summary>
    public static bool TryEdit(Repository repo, ref List<RebaseStep> steps, out string? error)
    {
        error = null;

        string? editor = Environment.GetEnvironmentVariable("SM_EDITOR")
                      ?? Environment.GetEnvironmentVariable("EDITOR")
                      ?? Environment.GetEnvironmentVariable("VISUAL");

        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (editor == null && !interactive)
        {
            // No editor and no console: the generated plan is used as-is so the operation still
            // has a deterministic meaning instead of blocking forever.
            SmLog.WriteWarning("no editor available; using the generated rebase plan as-is");
            return true;
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"sm-rebase-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, Render(steps));
            editor ??= Helpers.IsWindows ? "notepad.exe" : "vi";

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = $"\"{tmp}\"",
                UseShellExecute = false
            });
            proc?.WaitForExit();

            string edited = File.ReadAllText(tmp);
            var original = steps;
            if (!TryParse(edited, original, out var parsed, out error))
                return false;

            steps = parsed;
            return true;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
