namespace SourceManager;

/// <summary>
/// `sm hash-object` — compute the object id of arbitrary content.
///
/// The write half of the object database: it lets a caller learn what id a piece of content
/// would have (−w to actually store it, --stdin to read it from a pipe) without committing.
/// </summary>
public static class HashObjectCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool write = args.GetBoolOption("write");
        bool stdin = args.GetBoolOption("stdin");
        bool json = JsonOut.Requested(args);
        string? typeName = args.GetOption("type") ?? "blob";

        ObjectType type = typeName.ToLowerInvariant() switch
        {
            "blob" => ObjectType.Blob,
            "tree" => ObjectType.Tree,
            "commit" => ObjectType.Commit,
            "tag" => ObjectType.Tag,
            _ => (ObjectType)255
        };
        if ((byte)type == 255)
        {
            Terminal.WriteError($"fatal: invalid object type '{typeName}'");
            return 1;
        }

        var results = new List<(string Source, Hash Id, int Size)>();

        if (stdin)
        {
            using var input = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            byte[] content = ms.ToArray();
            Hash id = HashFor(type, content);
            if (write) repo.Objects.WriteObject(type, content);
            results.Add(("<stdin>", id, content.Length));
        }
        else
        {
            var files = args.PositionalArgs;
            if (files.Count == 0)
            {
                Terminal.WriteError("error: usage: sm hash-object [-w] [--stdin] <file>...");
                return 1;
            }

            foreach (string file in files)
            {
                string full = Path.GetFullPath(file, repo.RootPath);
                if (!File.Exists(full))
                {
                    Terminal.WriteError($"fatal: could not open '{file}' for reading");
                    return 1;
                }
                byte[] content = File.ReadAllBytes(full);
                Hash id = HashFor(type, content);
                if (write) repo.Objects.WriteObject(type, content);
                results.Add((file, id, content.Length));
            }
        }

        if (json)
        {
            JsonOut.Write("hash-object", new
            {
                write,
                type = typeName.ToLowerInvariant(),
                results = results.Select(r => new
                {
                    source = r.Source,
                    id = r.Id.ToHex(),
                    size = r.Size
                }).ToList()
            });
            return 0;
        }

        foreach (var r in results)
            Console.WriteLine(r.Id.ToHex());

        return 0;
    }

    private static Hash HashFor(ObjectType type, byte[] content) => type switch
    {
        ObjectType.Blob => HashUtil.ComputeBlob(content),
        ObjectType.Tree => HashUtil.ComputeTree(content),
        ObjectType.Commit => HashUtil.ComputeCommit(content),
        ObjectType.Tag => HashUtil.ComputeTag(content),
        _ => Hash.Zero
    };
}
