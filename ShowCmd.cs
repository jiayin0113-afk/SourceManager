using System.Text;

namespace SourceManager;

/// <summary>
/// `sm show` — display a commit, tag, tree or blob.
///
/// One command for "tell me about this object", with the same output shapes as the rest of the
/// tool: prose by default, a JSON document with --json.
/// </summary>
public static class ShowCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool stat = args.GetBoolOption("stat");
        bool nameOnly = args.GetBoolOption("name-only");
        bool nameStatus = args.GetBoolOption("name-status");
        bool patch = args.GetBoolOption("patch") || (!stat && !nameOnly && !nameStatus);

        string spec = args.GetPositional(0) ?? "HEAD";
        string? pathFilter = args.Positional(1);

        // "<rev>:<path>" selects a file inside a revision; handle it before plain revision
        // resolution, which would reject the colon form.
        if (RevisionResolver.TryResolveRevisionPath(repo, spec, out Hash blobId, out string blobPath))
        {
            byte[]? blobContent = repo.Objects.ReadBlob(blobId);
            if (blobContent == null)
            {
                Terminal.WriteError("fatal: blob is missing from the object database");
                return 1;
            }

            if (json)
            {
                bool binary = Helpers.IsBinary(blobContent);
                JsonOut.Write("show", new
                {
                    type = "blob",
                    id = blobId.ToHex(),
                    path = blobPath,
                    binary,
                    content = binary ? null : Encoding.UTF8.GetString(blobContent)
                });
            }
            else
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(blobContent, 0, blobContent.Length);
            }
            return 0;
        }

        RevisionTarget target;
        try
        {
            target = RevisionResolver.Resolve(repo, spec);
        }
        catch (RevisionException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        // A bare path that is not a revision means "show this file at HEAD".
        if (pathFilter == null && !FileIsRevision(repo, spec) && repo.Index.Contains(spec))
        {
            pathFilter = spec;
            target = RevisionResolver.Resolve(repo, "HEAD");
        }

        Commit? commit = repo.Objects.ReadCommit(target.Commit);
        if (commit == null)
        {
            Terminal.WriteError($"fatal: '{spec}' is not a commit");
            return 1;
        }

        var files = commit.TreeHash.HasValue
            ? repo.GetTreeEntries(commit.TreeHash.Value)
            : new Dictionary<string, Hash>();

        // A path argument turns `show <rev> <path>` into "show that file's content".
        if (pathFilter != null)
        {
            if (!files.TryGetValue(pathFilter, out Hash blobHash))
            {
                Terminal.WriteError($"fatal: path '{pathFilter}' does not exist in '{target.Commit.Short}'");
                return 1;
            }

            byte[]? content = repo.Objects.ReadBlob(blobHash);
            if (content == null)
            {
                Terminal.WriteError("fatal: blob is missing from the object database");
                return 1;
            }

            if (json)
            {
                JsonOut.Write("show", new
                {
                    type = "blob",
                    id = blobHash.ToHex(),
                    path = pathFilter,
                    binary = Helpers.IsBinary(content),
                    content = Helpers.IsBinary(content) ? null : Encoding.UTF8.GetString(content)
                });
            }
            else
            {
                Console.Out.Write(Encoding.UTF8.GetString(content));
            }
            return 0;
        }

        var diffs = commit.ParentHashes.Count > 0
            ? repo.Diff.DiffCommits(commit.ParentHashes[0], target.Commit)
            : repo.Diff.DiffCommits(target.Commit, target.Commit);

        if (json)
        {
            JsonOut.Write("show", new
            {
                type = "commit",
                id = target.Commit.ToHex(),
                short_id = target.Commit.Short,
                tree = JsonOut.Hex(commit.TreeHash),
                parents = commit.ParentHashes.Select(p => p.ToHex()).ToList(),
                author = new
                {
                    name = commit.Author.Name,
                    email = commit.Author.Email,
                    date = JsonOut.Timestamp(commit.Author.When)
                },
                committer = new
                {
                    name = commit.Committer.Name,
                    email = commit.Committer.Email,
                    date = JsonOut.Timestamp(commit.Committer.When)
                },
                subject = commit.Message.Split('\n')[0],
                body = commit.Message,
                files = diffs.Select(d => new
                {
                    path = d.NewPath,
                    oldPath = d.Kind == ChangeKind.Renamed ? d.OldPath : null,
                    status = JsonOut.StatusCode(d.Kind),
                    binary = d.IsBinary,
                    added = d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Added)),
                    deleted = d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Deleted))
                }).ToList()
            });
            return 0;
        }

        Terminal.WriteLine($"commit {target.Commit.ToHex()}", Terminal.Color.Yellow);
        if (target.IsTag) Terminal.WriteLine($"Tag:       {spec}");
        Terminal.WriteLine($"Author:    {commit.Author.Name} <{commit.Author.Email}>");
        Terminal.WriteLine($"Date:      {commit.Author.When:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine();
        foreach (string line in commit.Message.TrimEnd('\n').Split('\n'))
            Terminal.WriteLine($"    {line}");
        Console.WriteLine();

        if (stat)
        {
            PrintStat(diffs);
        }
        else if (nameOnly)
        {
            foreach (var d in diffs) Console.WriteLine(d.NewPath);
        }
        else if (nameStatus)
        {
            foreach (var d in diffs)
                Console.WriteLine($"{JsonOut.StatusCode(d.Kind)[0].ToString().ToUpperInvariant()}\t{d.NewPath}");
        }
        else if (patch && diffs.Count > 0)
        {
            DiffCmd.PrintDiffs(diffs, nameOnly: false, nameStatus: false, stat: false,
                shortstat: false, patch: true, raw: false);
        }

        return 0;
    }

    private static void PrintStat(List<FileDiff> diffs)
    {
        int totalAdded = 0, totalDeleted = 0;
        foreach (var d in diffs)
        {
            int added = d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Added));
            int deleted = d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Deleted));
            totalAdded += added;
            totalDeleted += deleted;
            Terminal.WriteLine($" {d.NewPath} | {added + deleted} {(added + deleted == 1 ? "line" : "lines")}");
        }
        Terminal.WriteLine($" {diffs.Count} file(s) changed, {totalAdded} insertion(+), {totalDeleted} deletion(-)");
    }

    private static bool FileIsRevision(Repository repo, string spec)
        => RevisionResolver.TryResolve(repo, spec, out _);
}
