namespace SourceManager;

public static class FetchCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool all = args.GetBoolOption("all");
        bool quiet = args.GetBoolOption("quiet");
        bool verbose = args.GetBoolOption("verbose");
        bool prune = args.GetBoolOption("prune");
        bool tags = args.GetBoolOption("tags");
        bool noTags = args.GetBoolOption("no-tags");
        bool force = args.GetBoolOption("force");
        int? depth = args.GetIntOption("depth");
        bool shallowSince = args.GetOption("shallow-since") != null;
        bool shallowExclude = args.GetOption("shallow-exclude") != null;
        bool unshallow = args.GetBoolOption("unshallow");
        bool noRecurseSubmodules = args.GetBoolOption("no-recurse-submodules");

        string? remoteName = args.GetPositional(0);
        string? refspec = args.GetPositional(1);

        if (remoteName == null)
        {
            string? currentBranch = repo.Refs.GetCurrentBranch();
            if (currentBranch != null)
                remoteName = repo.Refs.GetBranchRemote(currentBranch);
            if (remoteName == null)
                remoteName = GetDefaultRemote(repo);
        }

        if (all)
        {
            return FetchAll(repo, prune, quiet, verbose, force);
        }

        if (remoteName == null)
        {
            Terminal.WriteError("fatal: No remote repository specified.");
            return 1;
        }

        string? remoteUrl = repo.Config.Get("remote", $"{remoteName}.url");
        if (remoteUrl == null)
        {
            Terminal.WriteError($"fatal: No configured fetch URL for remote '{remoteName}'");
            return 1;
        }

        if (!IsSupportedUrl(remoteUrl))
        {
            Terminal.WriteError($"fatal: Unsupported transport protocol. Remote '{remoteUrl}' uses an unsupported scheme.");
            return 1;
        }

        if (!RemoteExists(remoteUrl))
        {
            Terminal.WriteError($"fatal: '{remoteUrl}' does not appear to be a SourceManager repository");
            return 1;
        }

        return FetchFromRemote(repo, remoteName, remoteUrl, refspec, prune, quiet, verbose, force);
    }

    private static bool IsSupportedUrl(string url)
    {
        return url.StartsWith("file://") || url.StartsWith("sm://") || url.StartsWith("http://") ||
               url.StartsWith("https://") || url.StartsWith("ssh://");
    }

    private static bool RemoteExists(string url)
    {
        using var transport = Transport.Create(url);
        try { return transport.IsRepository(); }
        catch { return false; }
    }

    private static int FetchAll(Repository repo, bool prune, bool quiet, bool verbose, bool force)
    {
        var remotes = new List<string>();
        foreach (string section in repo.Config.GetSections())
        {
            if (section != "remote") continue;
            foreach (var (key, _) in repo.Config.GetSection(section))
            {
                int dotIdx = key.IndexOf('.');
                if (dotIdx >= 0 && key.EndsWith(".url"))
                {
                    string name = key[..dotIdx];
                    if (!remotes.Contains(name))
                        remotes.Add(name);
                }
            }
        }

        foreach (string remoteName in remotes)
        {
            if (!quiet)
                Terminal.WriteLine($"Fetching {remoteName}", Terminal.Color.BrightCyan);
            string? url = repo.Config.Get("remote", $"{remoteName}.url");
            if (url == null || !IsSupportedUrl(url)) continue;
            if (!RemoteExists(url)) continue;
            FetchFromRemote(repo, remoteName, url, null, prune, quiet, verbose, force);
        }
        return 0;
    }

    private static int FetchFromRemote(Repository repo, string remoteName, string remoteUrl,
        string? refspec, bool prune, bool quiet, bool verbose, bool force)
    {
        using var transport = Transport.Create(remoteUrl, remoteName);

        var remoteRefs = transport.ListBranches();
        int fetched = 0;
        int updated = 0;

        var allWanted = new List<Hash>();

        foreach (var remoteBranch in remoteRefs)
        {
            if (refspec != null && remoteBranch.Name != refspec) continue;

            Hash? existingHash = repo.Refs.GetRemoteBranch(remoteName, remoteBranch.Name);

            if (existingHash.HasValue && existingHash.Value.Equals(remoteBranch.TipHash))
            {
                if (verbose)
                {
                    string statusFlag = force ? "[forced]" : "[up to date]";
                    Terminal.WriteLine($" * {statusFlag}   {remoteName}/{remoteBranch.Name} -> {remoteBranch.TipHash.Short}");
                }
                continue;
            }

            allWanted.Add(remoteBranch.TipHash);
            repo.Refs.SetRemoteBranch(remoteName, remoteBranch.Name, remoteBranch.TipHash);

            string flag = existingHash.HasValue ? "[updated]" : "[new branch]";
            if (!quiet)
                Terminal.WriteLine($" * {flag}   {remoteName}/{remoteBranch.Name} -> {remoteBranch.TipHash.Short}");

            fetched++;
            updated++;
        }

        var remoteTags = transport.ListTags();
        foreach (string tag in remoteTags)
        {
            Hash? tagHash = transport.GetTag(tag);
            if (!tagHash.HasValue) continue;

            Hash? existingTag = repo.Refs.GetTag(tag);
            if (existingTag.HasValue && existingTag.Value.Equals(tagHash.Value)) continue;

            repo.Refs.SetTag(tag, tagHash.Value);
            allWanted.Add(tagHash.Value);
            if (verbose && !quiet)
                Terminal.WriteLine($" * [new tag]    {tag} -> {tagHash.Value.Short}");
        }

        if (prune)
        {
            var localRemoteBranches = repo.Refs.ListRemoteBranches(remoteName);
            foreach (var (rn, bn, hash) in localRemoteBranches)
            {
                if (refspec != null && bn != refspec) continue;
                bool existsOnRemote = remoteRefs.Any(rb => rb.Name == bn);
                if (!existsOnRemote)
                {
                    repo.Refs.DeleteRemoteBranch(remoteName, bn);
                    if (!quiet)
                        Terminal.WriteLine($" - [deleted]  (none)  {remoteName}/{bn}");
                }
            }
        }

        if (allWanted.Count > 0)
        {
            var localHaves = new List<Hash>();
            foreach (var branch in repo.Refs.ListBranches())
                if (!branch.TipHash.Equals(Hash.Zero))
                    localHaves.Add(branch.TipHash);
            foreach (string tag in repo.Refs.ListTags())
            {
                Hash? th = repo.Refs.GetTag(tag);
                if (th.HasValue) localHaves.Add(th.Value);
            }

            var objects = transport.CollectObjects(allWanted, localHaves);
            foreach (var (hash, (type, data)) in objects)
            {
                WriteObjectLocal(repo, hash, data, type);
            }
        }

        if (!quiet)
        {
            Terminal.WriteSuccess($"Fetched {remoteName}: {fetched} refs updated");
        }

        return 0;
    }

    private static void WriteObjectLocal(Repository repo, Hash hash, byte[] data, ObjectType type)
    {
        // Transport payloads are uncompressed; WriteObjectRaw expects storage-form bytes.
        repo.Objects.WritePayload(hash, type, data);
    }

    private static string? GetDefaultRemote(Repository repo)
    {
        var remotes = new HashSet<string>();
        foreach (string section in repo.Config.GetSections())
        {
            if (section == "remote")
            {
                foreach (var (key, _) in repo.Config.GetSection(section))
                {
                    int dotIdx = key.IndexOf('.');
                    if (dotIdx >= 0)
                        remotes.Add(key[..dotIdx]);
                }
            }
        }
        return remotes.Count == 1 ? remotes.First() : remotes.FirstOrDefault(r => r == "origin");
    }
}