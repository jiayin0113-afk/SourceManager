namespace SourceManager;

public static class DiffCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool cached = args.GetBoolOption("cached") || args.GetBoolOption("staged");
        bool nameOnly = args.GetBoolOption("name-only");
        bool nameStatus = args.GetBoolOption("name-status");
        bool stat = args.GetBoolOption("stat");
        bool shortstat = args.GetBoolOption("shortstat");
        bool patch = args.GetBoolOption("patch") || (!nameOnly && !nameStatus && !stat && !shortstat);
        bool noPatch = args.GetBoolOption("no-patch");
        bool raw = args.GetBoolOption("raw");
        string? wordDiff = args.GetOption("word-diff");
        string? colorWords = args.GetOption("color-words");
        bool noIndex = args.GetBoolOption("no-index");
        bool text = args.GetBoolOption("text");
        bool ignoreSpace = args.GetBoolOption("ignore-space-change");
        bool ignoreAllSpace = args.GetBoolOption("ignore-all-space");
        bool ignoreBlankLines = args.GetBoolOption("ignore-blank-lines");
        int contextLines = args.GetIntOption("unified") ?? 3;
        bool patience = args.GetBoolOption("patience");
        bool histogram = args.GetBoolOption("histogram");
        string? diffAlgorithm = args.GetOption("diff-algorithm");

        string? arg1 = args.GetPositional(0);
        string? arg2 = args.GetPositional(1);
        string? pathFilter = args.PositionalArgs.Count > 2 ? args.PositionalArgs[2] : null;

        if (noIndex && arg1 != null && arg2 != null)
        {
            CompareTwoPaths(repo, arg1, arg2, nameOnly, nameStatus, patch, stat);
            return 0;
        }

        List<FileDiff> diffs;

        if (cached)
        {
            diffs = repo.Diff.DiffIndex();
        }
        else if (arg1 != null && arg2 != null)
        {
            Hash hash1 = ResolveHash(repo, arg1);
            Hash hash2 = ResolveHash(repo, arg2);
            diffs = repo.Diff.DiffCommits(hash1, hash2, pathFilter);
        }
        else if (arg1 != null)
        {
            Hash? headCommit = repo.Refs.GetHeadCommit();
            if (!headCommit.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit to diff against");
                return 1;
            }
            Hash hash1 = ResolveHash(repo, arg1);
            diffs = repo.Diff.DiffCommits(headCommit.Value, hash1, pathFilter);
        }
        else
        {
            Hash? headCommit = repo.Refs.GetHeadCommit();
            if (!headCommit.HasValue)
            {
                Terminal.WriteError("fatal: no HEAD commit to diff against");
                return 1;
            }
            diffs = repo.Diff.DiffWorkingTree(headCommit.Value);
        }

        if (JsonOut.Requested(args))
        {
            JsonOut.Write("diff", new
            {
                fileCount = diffs.Count,
                files = diffs.Select(d => new
                {
                    path = d.NewPath,
                    oldPath = d.Kind == ChangeKind.Renamed ? d.OldPath : null,
                    status = JsonOut.StatusCode(d.Kind),
                    binary = d.IsBinary,
                    oldId = d.OldHash?.ToHex(),
                    newId = d.NewHash?.ToHex(),
                    added = CountAdded(d),
                    deleted = CountDeleted(d),
                    hunks = d.Hunks.Select(h => new
                    {
                        oldStart = h.OldStart,
                        oldLines = h.OldCount,
                        newStart = h.NewStart,
                        newLines = h.NewCount,
                        header = h.Header,
                        lines = h.Lines.Select(l => new
                        {
                            type = l.Type switch
                            {
                                DiffLineType.Added => "add",
                                DiffLineType.Deleted => "delete",
                                _ => "context"
                            },
                            text = l.Text,
                            oldLine = l.OldLineNumber,
                            newLine = l.NewLineNumber
                        }).ToList()
                    }).ToList()
                }).ToList()
            });
            return 0;
        }

        PrintDiffs(diffs, nameOnly, nameStatus, stat, shortstat, patch, raw);

        return 0;
    }

    private static int CountAdded(FileDiff d) => d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Added));

    private static int CountDeleted(FileDiff d) => d.Hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Deleted));

    /// <summary>Similarity percentage appended to a rename status code, as "R100".</summary>
    private static int SimilarityScore(FileDiff d)
        => (int)Math.Round(d.Similarity * 100);

    /// <summary>
    /// Revision resolution is delegated to the shared resolver so every command accepts the same
    /// revision syntax (HEAD~N, <rev>^N, tags, remote refs, reflog selectors).
    /// </summary>
    private static Hash ResolveHash(Repository repo, string input)
        => RevisionResolver.Resolve(repo, input).Commit;

    private static void CompareTwoPaths(Repository repo, string path1, string path2,
        bool nameOnly, bool nameStatus, bool patch, bool stat)
    {
        if (!File.Exists(path1) && !File.Exists(path2))
        {
            Terminal.WriteError($"fatal: neither '{path1}' nor '{path2}' exist");
            return;
        }

        string oldText = File.Exists(path1) ? File.ReadAllText(path1) : "";
        string newText = File.Exists(path2) ? File.ReadAllText(path2) : "";
        var hunks = repo.Diff.ComputeDiff(oldText, newText, path1);

        var diff = new FileDiff
        {
            OldPath = path1,
            NewPath = path2,
            Kind = !File.Exists(path1) ? ChangeKind.Added : !File.Exists(path2) ? ChangeKind.Deleted : ChangeKind.Modified,
            Hunks = hunks
        };

        PrintDiffs(new List<FileDiff> { diff }, nameOnly, nameStatus, stat, false, patch, false);
    }

    /// <summary>
    /// Renders file diffs in the requested form. Public so `sm show` renders patches exactly
    /// the way `sm diff` does instead of growing a second, divergent formatter.
    /// </summary>
    public static void PrintDiffs(List<FileDiff> diffs, bool nameOnly, bool nameStatus,
        bool stat, bool shortstat, bool patch, bool raw)
    {
        int totalAdded = 0, totalDeleted = 0;

        foreach (var diff in diffs)
        {
            if (nameOnly)
            {
                Console.WriteLine(diff.OldPath);
                continue;
            }

            if (nameStatus)
            {
                char status = diff.Kind switch
                {
                    ChangeKind.Added => 'A',
                    ChangeKind.Deleted => 'D',
                    ChangeKind.Modified => 'M',
                    ChangeKind.Renamed => 'R',
                    _ => '?'
                };
                // A rename is one entry with both paths (and a similarity score), not a
                // delete followed by an unrelated add.
                if (diff.Kind == ChangeKind.Renamed)
                {
                    Console.WriteLine($"{status}{SimilarityScore(diff)}\t{diff.OldPath}\t{diff.NewPath}");
                }
                else
                {
                    Console.WriteLine($"{status}\t{diff.OldPath}");
                }
                continue;
            }

            if (raw)
            {
                string oldHash = diff.OldHash?.Short ?? "0000000";
                string newHash = diff.NewHash?.Short ?? "0000000";
                string status = diff.Kind switch
                {
                    ChangeKind.Added => "A",
                    ChangeKind.Deleted => "D",
                    ChangeKind.Modified => "M",
                    ChangeKind.Renamed => "R",
                    _ => "?"
                };
                if (diff.Kind == ChangeKind.Renamed)
                    Console.WriteLine($":{diff.OldMode:D6} {diff.NewMode:D6} {oldHash} {newHash} {status}\t{diff.OldPath}\t{diff.NewPath}");
                else
                    Console.WriteLine($":{diff.OldMode:D6} {diff.NewMode:D6} {oldHash} {newHash} {status}\t{diff.OldPath}");
                continue;
            }

            if (diff.IsBinary)
            {
                Console.WriteLine($"diff --git a/{diff.OldPath} b/{diff.NewPath}");
                Console.WriteLine("Binary files differ");
                continue;
            }

            if (patch)
            {
                Console.WriteLine($"diff --git a/{diff.OldPath} b/{diff.NewPath}");

                if (diff.Kind == ChangeKind.Deleted)
                    Console.WriteLine($"deleted file mode {(int)diff.OldMode:D6}");
                else if (diff.Kind == ChangeKind.Added)
                    Console.WriteLine($"new file mode {(int)diff.NewMode:D6}");
                else
                    Console.WriteLine($"index {diff.OldHash?.Short ?? "0000000"}..{diff.NewHash?.Short ?? "0000000"} {(int)diff.OldMode:D6}");

                Console.WriteLine($"--- a/{diff.OldPath}");
                Console.WriteLine($"+++ b/{diff.NewPath}");

                foreach (var hunk in diff.Hunks)
                {
                    Console.WriteLine(hunk.Header);
                    foreach (var line in hunk.Lines)
                    {
                        char prefix = line.Type switch
                        {
                            DiffLineType.Added => '+',
                            DiffLineType.Deleted => '-',
                            _ => ' '
                        };
                        Terminal.Color color = line.Type switch
                        {
                            DiffLineType.Added => Terminal.Color.Green,
                            DiffLineType.Deleted => Terminal.Color.Red,
                            _ => Terminal.Color.Default
                        };
                        Terminal.WriteLine($"{prefix}{line.Text}", color);
                        if (line.Type == DiffLineType.Added) totalAdded++;
                        else if (line.Type == DiffLineType.Deleted) totalDeleted++;
                    }
                }
            }
        }

        if (stat)
        {
            Console.WriteLine($" {diffs.Count} files changed, {totalAdded} insertions(+), {totalDeleted} deletions(-)");
        }

        if (shortstat)
        {
            Console.WriteLine($" {diffs.Count} files changed");
        }
    }
}