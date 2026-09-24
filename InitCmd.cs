namespace SourceManager;

public static class InitCmd
{
    public static int Execute(ParseResult args)
    {
        string? path = args.GetPositional(0);
        string targetDir = path != null ? Path.GetFullPath(path) : Environment.CurrentDirectory;
        string? branch = args.GetOption("branch") ?? "main";
        bool bare = args.GetBoolOption("bare");

        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        try
        {
            if (bare)
            {
                // A bare repository's git directory is the target itself, so it is opened in
                // bare mode rather than looked up as <target>/.sm.
                var bareRepo = new Repository(targetDir, bare: true);
                bareRepo.Init(branch, bare: true);
                Terminal.WriteSuccess($"Initialized empty SourceManager repository (bare) in {targetDir}");
            }
            else
            {
                var repo = new Repository(targetDir);
                repo.Init(branch);
                Terminal.WriteSuccess($"Initialized empty SourceManager repository in {Path.Combine(targetDir, ".sm")}");
            }
        }
        catch (InvalidOperationException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 128;
        }

        return 0;
    }
}
