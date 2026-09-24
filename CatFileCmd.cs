using System.Text;

namespace SourceManager;

/// <summary>
/// `sm cat-file` — read a single object out of the database.
///
/// The lowest-level read path: -t type, -s size, -p pretty, or the raw payload. Combined with
/// --json it is the primitive an agent uses to walk the object graph without shelling out to
/// several commands.
/// </summary>
public static class CatFileCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool type = args.GetBoolOption("type");
        bool size = args.GetBoolOption("size");
        bool pretty = args.GetBoolOption("pretty");
        bool exists = args.GetBoolOption("exists");
        bool batch = args.GetBoolOption("batch");

        if (batch)
            return Batch(repo, json);

        string? spec = args.GetPositional(0);
        if (spec == null)
        {
            Terminal.WriteError("error: usage: sm cat-file (-t|-s|-p|--exists) <object>");
            return 1;
        }

        if (!TryResolveObject(repo, spec, out Hash hash))
        {
            Terminal.WriteError($"fatal: Not a valid object name {spec}");
            return 1;
        }

        var typed = repo.Objects.ReadObjectWithType(hash);
        if (typed == null)
        {
            if (exists) return 1;
            Terminal.WriteError($"fatal: object {hash.Short} is missing from the object database");
            return 1;
        }

        string typeName = ObjectStore.TypeName(typed.Value.type);
        byte[] data = typed.Value.data;

        if (exists)
        {
            if (json) JsonOut.Write("cat-file", new { id = hash.ToHex(), exists = true, type = typeName });
            else Console.WriteLine(hash.ToHex());
            return 0;
        }

        if (type)
        {
            if (json) JsonOut.Write("cat-file", new { id = hash.ToHex(), type = typeName });
            else Console.WriteLine(typeName);
            return 0;
        }

        if (size)
        {
            if (json) JsonOut.Write("cat-file", new { id = hash.ToHex(), type = typeName, size = data.Length });
            else Console.WriteLine(data.Length);
            return 0;
        }

        if (json)
        {
            JsonOut.Write("cat-file", new
            {
                id = hash.ToHex(),
                type = typeName,
                size = data.Length,
                binary = typed.Value.type == ObjectType.Blob && Helpers.IsBinary(data),
                content = typed.Value.type == ObjectType.Blob && Helpers.IsBinary(data)
                    ? null
                    : Encoding.UTF8.GetString(data),
                tree = typed.Value.type == ObjectType.Tree ? DescribeTree(data) : null,
                commit = typed.Value.type == ObjectType.Commit ? DescribeCommit(data) : null
            });
            return 0;
        }

        if (typed.Value.type == ObjectType.Blob)
        {
            // Raw bytes to stdout, unchanged: this is the "give me the file" path.
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(data, 0, data.Length);
            return 0;
        }

        Console.Out.Write(Encoding.UTF8.GetString(data));
        if (data.Length > 0 && data[^1] != (byte)'\n') Console.WriteLine();
        return 0;
    }

    private static int Batch(Repository repo, bool json)
    {
        if (json)
        {
            Terminal.WriteError("error: --batch reads object names from stdin and is not available with --json");
            return 1;
        }

        string line;
        while ((line = Console.In.ReadLine() ?? "") != null)
        {
            line = line.Trim();
            if (line.Length == 0) break;

            if (!TryResolveObject(repo, line, out Hash hash))
            {
                Console.WriteLine($"{line} missing");
                continue;
            }

            var typed = repo.Objects.ReadObjectWithType(hash);
            if (typed == null)
            {
                Console.WriteLine($"{line} missing");
                continue;
            }

            Console.WriteLine($"{hash.ToHex()} {ObjectStore.TypeName(typed.Value.type)} {typed.Value.data.Length}");
        }

        return 0;
    }

    /// <summary>
    /// Accepts an object id (full or abbreviated), or a revision expression whose commit id is
    /// then used. Anything unresolvable returns false rather than throwing.
    /// </summary>
    private static bool TryResolveObject(Repository repo, string spec, out Hash hash)
    {
        hash = default;
        if (Hash.TryParse(spec) is Hash full && repo.Objects.ObjectExists(full))
        {
            hash = full;
            return true;
        }

        if (spec.Length is >= 4 and < 64 && IsHex(spec))
        {
            try
            {
                Hash partial = Helpers.ResolvePartialHash(repo, spec);
                hash = partial;
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (RevisionResolver.TryResolve(repo, spec, out var rev))
        {
            hash = rev.TagHash ?? rev.Commit;
            return true;
        }

        return false;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    private static List<object> DescribeTree(byte[] data)
    {
        var entries = new List<object>();
        Tree? tree = ObjectStore.DeserializeTree(data);
        if (tree == null) return entries;
        foreach (var entry in tree.Entries)
        {
            entries.Add(new
            {
                mode = FileModeFormat.ToOctal(entry.Mode),
                type = entry.Mode == FileMode.Directory ? "tree" : "blob",
                id = entry.ObjectHash.ToHex(),
                name = entry.Name
            });
        }
        return entries;
    }

    private static object? DescribeCommit(byte[] data)
    {
        Commit? commit = ObjectStore.DeserializeCommit(data);
        if (commit == null) return null;
        return new
        {
            tree = JsonOut.Hex(commit.TreeHash),
            parents = commit.ParentHashes.Select(p => p.ToHex()).ToList(),
            author = new { name = commit.Author.Name, email = commit.Author.Email, date = JsonOut.Timestamp(commit.Author.When) },
            committer = new { name = commit.Committer.Name, email = commit.Committer.Email, date = JsonOut.Timestamp(commit.Committer.When) },
            message = commit.Message
        };
    }
}
