namespace SourceManager;

public static class RemoteCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool verbose = args.GetBoolOption("verbose");
        bool add = args.GetBoolOption("add");
        bool remove = args.GetBoolOption("remove");
        bool rename = args.GetBoolOption("rename");
        bool setUrl = args.GetBoolOption("set-url");
        bool getUrl = args.GetBoolOption("get-url");
        bool show = args.GetBoolOption("show");
        bool prune = args.GetBoolOption("prune");
        bool fetch = args.GetBoolOption("fetch");

        string? name = args.GetPositional(0);
        string? url = args.GetPositional(1);

        // Accept the verb form (`sm remote add origin <url>`) alongside the flag form.
        // Only --verbose was ever registered, so every management operation used to fall
        // through to the listing branch and print the remote list while appearing to work.
        if (!add && !remove && !rename && !setUrl && !getUrl && !show && !prune && !fetch && name != null)
        {
            switch (name)
            {
                case "add":
                    add = true;
                    name = args.GetPositional(1);
                    url = args.GetPositional(2);
                    break;
                case "remove":
                case "rm":
                    remove = true;
                    name = args.GetPositional(1);
                    url = null;
                    break;
                case "rename":
                    rename = true;
                    name = args.GetPositional(1);
                    url = args.GetPositional(2);
                    break;
                case "set-url":
                    setUrl = true;
                    name = args.GetPositional(1);
                    url = args.GetPositional(2);
                    break;
                case "get-url":
                    getUrl = true;
                    name = args.GetPositional(1);
                    url = null;
                    break;
                case "show":
                    show = true;
                    name = args.GetPositional(1);
                    url = null;
                    break;
                case "prune":
                    prune = true;
                    name = args.GetPositional(1);
                    url = null;
                    break;
            }
        }

        if (add)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                Terminal.WriteError("error: usage: sm remote add <name> <url>");
                return 1;
            }
            return AddRemote(repo, name, url);
        }

        if (remove)
        {
            if (string.IsNullOrEmpty(name))
            {
                Terminal.WriteError("error: usage: sm remote remove <name>");
                return 1;
            }
            return RemoveRemote(repo, name);
        }

        if (rename)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                Terminal.WriteError("error: usage: sm remote rename <old> <new>");
                return 1;
            }
            return RenameRemote(repo, name, url);
        }

        if (setUrl)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                Terminal.WriteError("error: usage: sm remote set-url <name> <url>");
                return 1;
            }
            return SetRemoteUrl(repo, name, url);
        }

        if (getUrl)
        {
            if (name == null)
            {
                Terminal.WriteError("error: remote name required for --get-url");
                return 1;
            }
            string? remoteUrl = repo.Config.Get("remote", $"{name}.url");
            if (remoteUrl != null)
                Console.WriteLine(remoteUrl);
            return 0;
        }

        if (show && name != null)
        {
            return ShowRemote(repo, name, verbose);
        }

        return ListRemotes(repo, verbose);
    }

    private static int ListRemotes(Repository repo, bool verbose)
    {
        var remotes = GetRemotes(repo);
        if (remotes.Count == 0)
        {
            return 0;
        }

        foreach (var remote in remotes)
        {
            Console.WriteLine(remote.Name);

            if (verbose)
            {
                Console.WriteLine($"  URL: {remote.FetchUrl}");
                if (remote.PushUrl != null)
                    Console.WriteLine($"  Push URL: {remote.PushUrl}");

                var remoteBranches = repo.Refs.ListRemoteBranches(remote.Name);
                if (remoteBranches.Count > 0)
                {
                    Console.WriteLine("  Remote branches:");
                    foreach (var (rn, bn, hash) in remoteBranches)
                    {
                        string? currentBranch = repo.Refs.GetCurrentBranch();
                        string tracked = "";
                        string? upstream = repo.Refs.GetBranchUpstream(currentBranch ?? "");
                        if (upstream == $"{rn}/{bn}")
                            tracked = " (tracked)";
                        Console.WriteLine($"    {bn} -> {hash.Short}{tracked}");
                    }
                }
            }
        }

        return 0;
    }

    private static int ShowRemote(Repository repo, string name, bool verbose)
    {
        var remotes = GetRemotes(repo);
        var remote = remotes.FirstOrDefault(r => r.Name == name);
        if (remote.Name == null)
        {
            Terminal.WriteError($"error: No such remote '{name}'");
            return 1;
        }

        Console.WriteLine($"* remote {name}");
        Console.WriteLine($"  Fetch URL: {remote.FetchUrl}");
        if (remote.PushUrl != null)
            Console.WriteLine($"  Push  URL: {remote.PushUrl}");

        var remoteBranches = repo.Refs.ListRemoteBranches(name);
        if (remoteBranches.Count > 0)
        {
            Console.WriteLine("  Remote branches:");
            foreach (var (rn, bn, hash) in remoteBranches)
            {
                string? currentBranch = repo.Refs.GetCurrentBranch();
                string tracked = "";
                string? upstream = repo.Refs.GetBranchUpstream(currentBranch ?? "");
                if (upstream == $"{rn}/{bn}")
                    tracked = " (tracked)";
                Console.WriteLine($"    {bn} -> {hash.Short}{tracked}");
            }
        }
        else
        {
            Console.WriteLine("  Remote branch(es): (none)");
        }

        Console.WriteLine("  Local branch configured for 'sm pull':");
        bool foundLocal = false;
        foreach (var branch in repo.Refs.ListBranches())
        {
            string? rem = repo.Refs.GetBranchRemote(branch.Name);
            if (rem == name)
            {
                Console.WriteLine($"    {branch.Name} merges with remote {branch.UpstreamBranch ?? "?"}");
                foundLocal = true;
            }
        }
        if (!foundLocal)
            Console.WriteLine("    (none)");

        return 0;
    }

    private static int AddRemote(Repository repo, string name, string url)
    {
        var remotes = GetRemotes(repo);
        if (remotes.Any(r => r.Name == name))
        {
            Terminal.WriteError($"error: remote '{name}' already exists. Use 'sm remote set-url' to change URL.");
            return 1;
        }

        repo.Config.Set("remote", $"{name}.url", url);
        repo.Config.Set("remote", $"{name}.fetch", $"+refs/heads/*:refs/remotes/{name}/*");
        Terminal.WriteSuccess($"Added remote '{name}' -> {url}");
        return 0;
    }

    private static int RemoveRemote(Repository repo, string name)
    {
        var remotes = GetRemotes(repo);
        if (!remotes.Any(r => r.Name == name))
        {
            Terminal.WriteError($"error: No such remote: '{name}'");
            return 1;
        }

        var sections = repo.Config.GetSections().ToList();
        foreach (string section in sections)
        {
            if (section == "remote")
            {
                foreach (var (key, _) in repo.Config.GetSection(section))
                {
                    if (key.StartsWith($"{name}."))
                        repo.Config.Unset(section, key);
                }
            }
            if (section == "branch")
            {
                foreach (var (key, value) in repo.Config.GetSection(section))
                {
                    if (key.EndsWith(".remote") && value == name)
                    {
                        repo.Config.Unset(section, key);
                        int dotIdx = key.LastIndexOf('.');
                        if (dotIdx >= 0)
                            repo.Config.Unset("branch", $"{key[..dotIdx]}.merge");
                    }
                }
            }
        }

        string remoteDir = Path.Combine(repo.SmPath, "refs", "remotes", name);
        if (Directory.Exists(remoteDir))
            Directory.Delete(remoteDir, true);

        Terminal.WriteSuccess($"Removed remote '{name}'");
        return 0;
    }

    private static int RenameRemote(Repository repo, string oldName, string newName)
    {
        string? oldUrl = repo.Config.Get("remote", $"{oldName}.url");
        if (oldUrl == null)
        {
            Terminal.WriteError($"error: No such remote: '{oldName}'");
            return 1;
        }

        string? oldFetch = repo.Config.Get("remote", $"{oldName}.fetch");

        repo.Config.Set("remote", $"{newName}.url", oldUrl);
        if (oldFetch != null)
            repo.Config.Set("remote", $"{newName}.fetch", oldFetch.Replace(oldName, newName));

        repo.Config.Unset("remote", $"{oldName}.url");
        repo.Config.Unset("remote", $"{oldName}.fetch");

        string oldDir = Path.Combine(repo.SmPath, "refs", "remotes", oldName);
        string newDir = Path.Combine(repo.SmPath, "refs", "remotes", newName);
        if (Directory.Exists(oldDir))
            Directory.Move(oldDir, newDir);

        Terminal.WriteSuccess($"Renamed remote '{oldName}' to '{newName}'");
        return 0;
    }

    private static int SetRemoteUrl(Repository repo, string name, string url)
    {
        string? oldUrl = repo.Config.Get("remote", $"{name}.url");
        if (oldUrl == null)
        {
            Terminal.WriteError($"error: No such remote: '{name}'");
            return 1;
        }

        repo.Config.Set("remote", $"{name}.url", url);
        Terminal.WriteSuccess($"Updated remote '{name}' URL: {oldUrl} -> {url}");
        return 0;
    }

    private static List<(string Name, string FetchUrl, string? PushUrl)> GetRemotes(Repository repo)
    {
        var remotes = new List<(string, string, string?)>();
        var seen = new HashSet<string>();

        foreach (string section in repo.Config.GetSections())
        {
            if (section != "remote") continue;
            foreach (var (key, value) in repo.Config.GetSection(section))
            {
                int dotIdx = key.IndexOf('.');
                if (dotIdx < 0) continue;
                string remoteName = key[..dotIdx];
                string suffix = key[(dotIdx + 1)..];

                if (suffix == "url" && seen.Add(remoteName))
                {
                    string? pushUrl = repo.Config.Get("remote", $"{remoteName}.pushurl");
                    remotes.Add((remoteName, value, pushUrl));
                }
            }
        }
        return remotes;
    }
}