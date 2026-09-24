namespace SourceManager;

/// <summary>
/// `sm ls-tree` — list the contents of a tree object.
///
/// Where `ls-files` reads the index, this reads a committed tree, so it answers "what did this
/// revision contain" — including for revisions whose files are not in the working tree.
/// </summary>
public static class LsTreeCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool recursive = args.GetBoolOption("recursive");
        bool nameOnly = args.GetBoolOption("name-only");
        bool json = JsonOut.Requested(args);
        bool nullTerminated = args.GetBoolOption("null");

        string spec = args.GetPositional(0) ?? "HEAD";

        Hash treeHash;
        try
        {
            var target = RevisionResolver.Resolve(repo, spec);
            Commit? commit = repo.Objects.ReadCommit(target.Commit);
            if (commit?.TreeHash.HasValue != true)
            {
                Terminal.WriteError($"fatal: '{spec}' does not point at a tree");
                return 1;
            }
            treeHash = commit.TreeHash.Value;
        }
        catch (RevisionException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        var entries = Walk(repo, treeHash, "", recursive);

        if (json)
        {
            JsonOut.Write("ls-tree", new
            {
                tree = treeHash.ToHex(),
                recursive,
                count = entries.Count,
                entries = entries.Select(e => new
                {
                    mode = FileModeFormat.ToOctal(e.Mode),
                    type = e.Mode == FileMode.Directory ? "tree" : "blob",
                    id = e.Hash.ToHex(),
                    name = e.Path
                }).ToList()
            });
            return 0;
        }

        foreach (var entry in entries)
        {
            string suffix = nullTerminated ? "\0" : "\n";
            if (nameOnly)
            {
                Console.Out.Write(entry.Path + suffix);
            }
            else
            {
                string type = entry.Mode == FileMode.Directory ? "tree" : "blob";
                Console.Out.Write($"{FileModeFormat.ToOctal(entry.Mode)} {type} {entry.Hash.ToHex()}\t{entry.Path}{suffix}");
            }
        }

        return 0;
    }

    private readonly record struct TreeListing(FileMode Mode, Hash Hash, string Path);

    private static List<TreeListing> Walk(Repository repo, Hash treeHash, string prefix, bool recursive)
    {
        var result = new List<TreeListing>();
        Tree? tree = repo.Objects.ReadTree(treeHash);
        if (tree == null) return result;

        foreach (var entry in tree.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            string path = string.IsNullOrEmpty(prefix) ? entry.Name : $"{prefix}/{entry.Name}";

            if (entry.Mode == FileMode.Directory)
            {
                if (recursive)
                    result.AddRange(Walk(repo, entry.ObjectHash, path, true));
                else
                    result.Add(new TreeListing(entry.Mode, entry.ObjectHash, path));
            }
            else
            {
                result.Add(new TreeListing(entry.Mode, entry.ObjectHash, path));
            }
        }

        return result;
    }
}
