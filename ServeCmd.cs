namespace SourceManager;

public static class ServeCmd
{
    public static int Execute(ParseResult args)
    {
        bool stdio = args.GetBoolOption("stdio");
        bool http = args.GetBoolOption("http");
        int port = args.GetIntOption("port") ?? 9418;
        string bind = args.GetOption("bind") ?? "0.0.0.0";
        string? repoPath = args.GetOption("path");

        if (repoPath == null)
        {
            var found = Repository.Find();
            if (found == null)
            {
                Terminal.WriteError("fatal: not a SourceManager repository (or any of the parent directories)");
                return 1;
            }
            repoPath = found.RootPath;
        }

        if (!Repository.IsRepository(repoPath))
        {
            Terminal.WriteError($"fatal: '{repoPath}' does not appear to be a SourceManager repository");
            return 1;
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (stdio)
            {
                SmServer.StartStdioAsync(repoPath, cts.Token).GetAwaiter().GetResult();
            }
            else if (http)
            {
                SmServer.StartAsync(repoPath, bind, port, cts.Token).GetAwaiter().GetResult();
            }
            else
            {
                // The native sm:// protocol is the default carrier.
                SmServer.StartTcpAsync(repoPath, bind, port, cts.Token).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Terminal.WriteError($"Server error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}