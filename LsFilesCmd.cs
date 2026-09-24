using System.Text;

namespace SourceManager;

/// <summary>
/// `sm ls-files` — list the files the index tracks.
///
/// A read-only window onto the index, for scripts and for answering "what is actually staged".
/// </summary>
public static class LsFilesCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool stage = args.GetBoolOption("stage");
        bool modified = args.GetBoolOption("modified");
        bool deleted = args.GetBoolOption("deleted");
        bool others = args.GetBoolOption("others");
        bool cached = args.GetBoolOption("cached") || (!modified && !deleted && !others);
        bool nullTerminated = args.GetBoolOption("null");

        var status = repo.GetStatus();
        var entries = new List<(string Path, IndexEntry? Entry)>();

        if (cached)
        {
            foreach (var (path, entry) in repo.Index.GetAllEntries())
                entries.Add((path, entry));
        }

        if (modified)
        {
            foreach (var (path, kind) in status.Changes)
            {
                if (kind == ChangeKind.Modified && !entries.Any(e => e.Path == path))
                    entries.Add((path, repo.Index.GetEntry(path)));
            }
        }

        if (deleted)
        {
            foreach (var (path, kind) in status.Changes)
            {
                if (kind == ChangeKind.Deleted && !entries.Any(e => e.Path == path))
                    entries.Add((path, repo.Index.GetEntry(path)));
            }
        }

        if (others)
        {
            foreach (string path in status.Untracked)
            {
                if (!entries.Any(e => e.Path == path))
                    entries.Add((path, null));
            }
        }

        entries = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();

        if (json)
        {
            JsonOut.Write("ls-files", new
            {
                count = entries.Count,
                files = entries.Select(e => new
                {
                    path = e.Path,
                    id = e.Entry?.ObjectHash.ToHex(),
                    mode = e.Entry != null ? FileModeFormat.ToOctal(e.Entry.Mode) : null,
                    staged = e.Entry != null && e.Entry.ObjectHash.Equals(Hash.Zero) == false
                }).ToList()
            });
            return 0;
        }

        foreach (var (path, entry) in entries)
        {
            if (stage && entry != null)
            {
                // <mode> <object> <stage>\t<path>
                Console.Write($"{FileModeFormat.ToOctal(entry.Mode)} {entry.ObjectHash.ToHex()} {entry.Stage}\t");
            }

            if (nullTerminated)
            {
                Console.Out.Write(path);
                Console.Out.Write('\0');
            }
            else
            {
                Console.WriteLine(path);
            }
        }

        return 0;
    }
}
