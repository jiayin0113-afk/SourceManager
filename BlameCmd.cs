using System.Text;

namespace SourceManager;

public static class BlameCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool showLong = args.GetBoolOption("long");
        bool showShort = args.GetBoolOption("short");
        bool showName = args.GetBoolOption("show-name");
        bool showEmail = args.GetBoolOption("show-email");
        bool showTimestamp = args.GetBoolOption("show-timestamp");
        bool showRawTimestamp = args.GetBoolOption("show-raw-timestamp");
        bool showNumber = args.GetBoolOption("show-number");
        bool incremental = args.GetBoolOption("incremental");
        bool showScore = args.GetBoolOption("show-score");
        bool root = args.GetBoolOption("root");
        bool showStats = args.GetBoolOption("show-stats");
        bool reverse = args.GetBoolOption("reverse");
        bool porcelain = args.GetBoolOption("porcelain");
        bool linePorcelain = args.GetBoolOption("line-porcelain");
        string? encoding = args.GetOption("encoding");
        string? contents = args.GetOption("contents");
        string? dateFormat = args.GetOption("date");
        int? firstLine = args.GetIntOption("L");
        int? lastLine = args.GetIntOption("last-line");

        string? filePath = args.GetPositional(0);
        string? revision = args.GetPositional(1);

        if (filePath == null)
        {
            Terminal.WriteError("error: file path required");
            return 1;
        }

        Hash? targetCommit;
        if (revision != null)
        {
            try
            {
                targetCommit = ResolveHash(repo, revision);
            }
            catch
            {
                Terminal.WriteError($"fatal: invalid revision: {revision}");
                return 1;
            }
        }
        else
        {
            targetCommit = repo.Refs.GetHeadCommit();
            if (!targetCommit.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit");
                return 1;
            }
        }

        return AnnotateFile(repo, filePath, targetCommit.Value, porcelain, linePorcelain,
            showLong, showShort, showName, showEmail, showTimestamp, showNumber, showStats);
    }

    private static int AnnotateFile(Repository repo, string filePath, Hash commitHash,
        bool porcelain, bool linePorcelain, bool showLong, bool showShort, bool showName,
        bool showEmail, bool showTimestamp, bool showNumber, bool showStats)
    {
        string fileContent = GetFileContentAtCommit(repo, filePath, commitHash);
        if (fileContent == null)
        {
            Terminal.WriteError($"fatal: no such path '{filePath}' in {commitHash.Short}");
            return 1;
        }

        string[] lines = fileContent.Split('\n');
        var blameLines = new BlameLineInfo[lines.Length];

        var commitCache = new Dictionary<Hash, Commit>();
        var history = repo.GetCommitHistory(null, int.MaxValue, filePath, pathIsPrefix: false);
        var historySet = new HashSet<string>(history.Select(h => h.ToHex()));

        for (int i = 0; i < lines.Length; i++)
        {
            blameLines[i] = new BlameLineInfo
            {
                CommitHash = commitHash,
                Line = i + 1,
                Text = lines[i]
            };
        }

        foreach (Hash currentHash in history)
        {
            Commit? currentCommit = repo.Objects.ReadCommit(currentHash);
            if (currentCommit == null) continue;

            commitCache[currentHash] = currentCommit;

            if (currentCommit.ParentHashes.Count == 0) continue;
            Hash parentHash = currentCommit.ParentHashes[0];

            string? currentContent = GetFileContentAtCommit(repo, filePath, currentHash);
            string? parentContent = GetFileContentAtCommit(repo, filePath, parentHash);

            if (currentContent == null || parentContent == null) continue;

            string[] currentLines = currentContent.Split('\n');
            string[] parentLines = parentContent.Split('\n');

            for (int lineIdx = 0; lineIdx < blameLines.Length; lineIdx++)
            {
                if (!blameLines[lineIdx].CommitHash.Equals(currentHash)) continue;

                int currentLine = blameLines[lineIdx].Line - 1;
                if (currentLine >= currentLines.Length) continue;

                string lineText = currentLines[currentLine];
                int matchedParentLine = -1;

                for (int pl = 0; pl < parentLines.Length; pl++)
                {
                    if (parentLines[pl] == lineText)
                    {
                        matchedParentLine = pl;
                        break;
                    }
                }

                if (matchedParentLine >= 0)
                {
                    blameLines[lineIdx].CommitHash = parentHash;
                    blameLines[lineIdx].Line = matchedParentLine + 1;
                }
            }
        }

        int maxHashWidth = 8;
        if (porcelain)
        {
            foreach (var blame in blameLines)
            {
                Console.WriteLine($"{blame.CommitHash.ToHex()} {blame.Line} {blame.Line}");
                Commit? commit = commitCache.GetValueOrDefault(blame.CommitHash);
                if (commit != null)
                {
                    Console.WriteLine($"author {commit.Author.Name}");
                    Console.WriteLine($"author-mail {commit.Author.Email}");
                    Console.WriteLine($"author-time {new DateTimeOffset(commit.Author.When.DateTime, TimeSpan.Zero).ToUnixTimeSeconds()}");
                    Console.WriteLine($"committer {commit.Committer.Name}");
                    Console.WriteLine($"committer-mail {commit.Committer.Email}");
                    Console.WriteLine($"committer-time {new DateTimeOffset(commit.Committer.When.DateTime, TimeSpan.Zero).ToUnixTimeSeconds()}");
                    Console.WriteLine($"summary {commit.Message.Split('\n')[0]}");
                }
                Console.WriteLine($"\t{blame.Text}");
            }
            return 0;
        }

        if (linePorcelain)
        {
            foreach (var blame in blameLines)
            {
                Commit? commit = commitCache.GetValueOrDefault(blame.CommitHash);
                if (commit != null)
                {
                    Console.WriteLine($"{blame.CommitHash.Short} {(blame.Line).ToString().PadLeft(4)} ({commit.Author.Name} {commit.Author.When:yyyy-MM-dd HH:mm:ss}) {blame.Text}");
                }
                else
                {
                    Console.WriteLine($"{blame.CommitHash.Short} {blame.Line.ToString().PadLeft(4)} (?) {blame.Text}");
                }
            }
            return 0;
        }

        foreach (var blame in blameLines)
        {
            Commit? commit = commitCache.GetValueOrDefault(blame.CommitHash);
            string hashDisplay = showLong ? blame.CommitHash.ToHex() : blame.CommitHash.Short;
            if (showShort) hashDisplay = blame.CommitHash.Short;

            Console.Write($"{hashDisplay}");

            if (showNumber)
                Console.Write($" {blame.Line.ToString().PadLeft(4)}");

            if (commit != null)
            {
                string authorDisplay = "";
                if (showName) authorDisplay += commit.Author.Name;
                if (showEmail) authorDisplay += (authorDisplay.Length > 0 ? " " : "") + $"<{commit.Author.Email}>";
                if (showTimestamp) authorDisplay += (authorDisplay.Length > 0 ? " " : "") + commit.Author.When.ToString("yyyy-MM-dd");
                if (string.IsNullOrEmpty(authorDisplay))
                    authorDisplay = $"({commit.Author.Name}";
                else
                    authorDisplay = $" ({authorDisplay}";
                Console.Write($"{authorDisplay})");
            }
            else
            {
                Console.Write(" (?)");
            }

            Console.WriteLine($" {blame.Text}");
        }

        if (showStats)
        {
            var authorLineCounts = new Dictionary<string, int>();
            foreach (var blame in blameLines)
            {
                string key = blame.CommitHash.Short;
                if (!authorLineCounts.ContainsKey(key))
                    authorLineCounts[key] = 0;
                authorLineCounts[key]++;
            }
            Console.WriteLine();
            foreach (var (hash, count) in authorLineCounts)
            {
                Console.WriteLine($"{hash}: {count} lines");
            }
        }

        return 0;
    }

    private static string? GetFileContentAtCommit(Repository repo, string filePath, Hash commitHash)
    {
        Commit? commit = repo.Objects.ReadCommit(commitHash);
        if (commit?.TreeHash == null) return null;

        Hash? blobHash = FindFileInTree(repo, commit.TreeHash.Value, filePath);
        if (blobHash == null) return null;

        byte[]? blob = repo.Objects.ReadBlob(blobHash.Value);
        return blob != null ? Encoding.UTF8.GetString(blob) : null;
    }

    private static Hash? FindFileInTree(Repository repo, Hash treeHash, string path)
    {
        Tree? tree = repo.Objects.ReadTree(treeHash);
        if (tree == null) return null;

        string[] parts = path.Split('/', 2, StringSplitOptions.None);

        foreach (var entry in tree.Entries)
        {
            if (entry.Name == parts[0])
            {
                if (parts.Length == 1)
                    return entry.ObjectHash;
                if (entry.Mode == FileMode.Directory)
                    return FindFileInTree(repo, entry.ObjectHash, parts[1]);
                return null;
            }
        }
        return null;
    }

    private static Hash ResolveHash(Repository repo, string input)
    {
        if (input == "HEAD")
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue) throw new InvalidOperationException("HEAD does not exist");
            return head.Value;
        }
        if (Hash.TryParse(input) is Hash h) return h;
        if (input.Length >= 4) return Helpers.ResolvePartialHash(repo, input);
        Hash? branch = repo.Refs.GetBranch(input);
        if (branch.HasValue) return branch.Value;
        Hash? tag = repo.Refs.GetTag(input);
        if (tag.HasValue) return tag.Value;
        throw new InvalidOperationException($"Unknown revision: {input}");
    }

    private struct BlameLineInfo
    {
        public Hash CommitHash;
        public int Line;
        public string Text;
    }
}