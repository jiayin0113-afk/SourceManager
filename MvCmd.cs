namespace SourceManager;

/// <summary>
/// `sm mv` — move or rename a tracked file, updating the index.
///
/// The working tree move and the index update are one operation: a plain filesystem move leaves
/// the index pointing at the old path, so the next commit would record a delete plus an add
/// instead of a rename.
/// </summary>
public static class MvCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool force = args.GetBoolOption("force");
        bool dryRun = args.GetBoolOption("dry-run");
        bool verbose = args.GetBoolOption("verbose");

        var paths = args.PositionalArgs;
        if (paths.Count < 2)
        {
            Terminal.WriteError("error: usage: sm mv <source>... <destination>");
            return 1;
        }

        var sources = paths.Take(paths.Count - 1).ToList();
        string destination = paths[^1];

        // Moving into an existing directory means "keep each file's name inside it".
        string destFull = Path.GetFullPath(destination, repo.RootPath);
        bool destIsDirectory = Directory.Exists(destFull);

        if (sources.Count > 1 && !destIsDirectory)
        {
            Terminal.WriteError($"fatal: destination '{destination}' is not a directory");
            return 1;
        }

        foreach (string source in sources)
        {
            string sourceFull = Path.GetFullPath(source, repo.RootPath);
            if (!PathUtil.IsSubPath(repo.RootPath, sourceFull))
            {
                Terminal.WriteError($"fatal: '{source}' is outside the repository");
                return 1;
            }

            string sourceRel = PathUtil.GetRelativePath(repo.RootPath, sourceFull);
            if (!File.Exists(sourceFull))
            {
                Terminal.WriteError($"fatal: bad source, source={source}, destination={destination}");
                return 1;
            }

            if (!repo.Index.Contains(sourceRel))
            {
                Terminal.WriteError($"fatal: not under version control, source={source}");
                return 1;
            }

            string targetFull = destIsDirectory
                ? Path.Combine(destFull, Path.GetFileName(sourceFull))
                : destFull;

            if (File.Exists(targetFull) && !force)
            {
                Terminal.WriteError($"fatal: destination exists, source={source}, destination={destination}");
                return 1;
            }

            string targetRel = PathUtil.GetRelativePath(repo.RootPath, targetFull);
            if (verbose || dryRun)
                Terminal.WriteLine($"renaming {sourceRel} -> {targetRel}");

            if (dryRun) continue;

            string? targetDir = Path.GetDirectoryName(targetFull);
            if (targetDir != null && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // Stage the destination BEFORE removing the source so the content is preserved even
            // if the move fails partway.
            File.Move(sourceFull, targetFull, overwrite: force);
            repo.StageFile(targetRel);
            repo.Index.Remove(sourceRel);
        }

        if (!dryRun)
            repo.Index.Save();

        return 0;
    }
}
