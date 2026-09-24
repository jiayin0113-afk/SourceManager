using System.Text;

namespace SourceManager;

public class DiffEngine
{
    private readonly Repository _repo;

    public DiffEngine(Repository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Minimum content similarity (0..1) for a delete/add pair to be reported as a rename.
    /// </summary>
    public const double RenameSimilarityThreshold = 0.5;

    public List<FileDiff> DiffCommits(Hash oldCommit, Hash newCommit, string? pathFilter = null,
        bool detectRenames = true)
    {
        var diffs = new List<FileDiff>();
        Commit? oldC = _repo.Objects.ReadCommit(oldCommit);
        Commit? newC = _repo.Objects.ReadCommit(newCommit);

        if (oldC == null || newC == null) return diffs;
        if (!oldC.TreeHash.HasValue || !newC.TreeHash.HasValue) return diffs;

        var oldFiles = _repo.GetTreeEntries(oldC.TreeHash.Value);
        var newFiles = _repo.GetTreeEntries(newC.TreeHash.Value);

        var allPaths = new HashSet<string>(oldFiles.Keys);
        allPaths.UnionWith(newFiles.Keys);

        var deleted = new List<string>();
        var added = new List<string>();
        var modified = new List<string>();

        foreach (string path in allPaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            bool inOld = oldFiles.TryGetValue(path, out Hash oldHash);
            bool inNew = newFiles.TryGetValue(path, out Hash newHash);

            if (inOld && inNew && oldHash.Equals(newHash)) continue;

            if (!inOld) added.Add(path);
            else if (!inNew) deleted.Add(path);
            else modified.Add(path);
        }

        // Rename detection is on by default: a moved or renamed file is one logical change with
        // history behind it, not a delete plus an unrelated add. This is what lets `log --follow`
        // and review tooling track content across a move.
        var renamed = detectRenames
            ? DetectRenames(oldFiles, newFiles, deleted, added)
            : new Dictionary<string, (string OldPath, double Similarity)>();

        foreach (string path in modified)
        {
            if (pathFilter != null && path != pathFilter) continue;
            var diff = new FileDiff
            {
                OldPath = path,
                NewPath = path,
                OldHash = oldFiles[path],
                NewHash = newFiles[path],
                Kind = ChangeKind.Modified
            };
            PopulateContentDiff(diff, oldFiles[path], newFiles[path], path);
            diffs.Add(diff);
        }

        foreach (var (newPath, rename) in renamed.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (pathFilter != null && newPath != pathFilter && rename.OldPath != pathFilter) continue;
            var diff = new FileDiff
            {
                OldPath = rename.OldPath,
                NewPath = newPath,
                OldHash = oldFiles[rename.OldPath],
                NewHash = newFiles[newPath],
                Kind = ChangeKind.Renamed,
                Similarity = rename.Similarity
            };
            PopulateContentDiff(diff, oldFiles[rename.OldPath], newFiles[newPath], newPath);
            diffs.Add(diff);
        }

        foreach (string path in added)
        {
            if (renamed.ContainsKey(path)) continue;
            if (pathFilter != null && path != pathFilter) continue;
            var diff = new FileDiff
            {
                OldPath = path,
                NewPath = path,
                NewHash = newFiles[path],
                Kind = ChangeKind.Added
            };
            PopulateContentDiff(diff, null, newFiles[path], path);
            diffs.Add(diff);
        }

        foreach (string path in deleted)
        {
            if (renamed.Values.Any(r => r.OldPath == path)) continue;
            if (pathFilter != null && path != pathFilter) continue;
            var diff = new FileDiff
            {
                OldPath = path,
                NewPath = path,
                OldHash = oldFiles[path],
                Kind = ChangeKind.Deleted
            };
            PopulateContentDiff(diff, oldFiles[path], null, path);
            diffs.Add(diff);
        }

        return diffs.OrderBy(d => d.NewPath, StringComparer.Ordinal).ToList();
    }

    private void PopulateContentDiff(FileDiff diff, Hash? oldHash, Hash? newHash, string path)
    {
        byte[]? oldContent = oldHash.HasValue ? _repo.Objects.ReadBlob(oldHash.Value) : null;
        byte[]? newContent = newHash.HasValue ? _repo.Objects.ReadBlob(newHash.Value) : null;

        bool oldBinary = oldContent != null && Helpers.IsBinary(oldContent);
        bool newBinary = newContent != null && Helpers.IsBinary(newContent);

        if (oldBinary || newBinary)
        {
            diff.IsBinary = true;
            return;
        }

        string oldText = oldContent != null ? Encoding.UTF8.GetString(oldContent) : "";
        string newText = newContent != null ? Encoding.UTF8.GetString(newContent) : "";
        diff.Hunks = ComputeDiff(oldText, newText, path);
    }

    /// <summary>
    /// Pairs deleted paths with added paths that hold the same or similar content, returning
    /// newPath -&gt; (oldPath, similarity). Exact content matches are resolved first, then the
    /// most similar remaining pairs above <see cref="RenameSimilarityThreshold"/>.
    /// </summary>
    private Dictionary<string, (string OldPath, double Similarity)> DetectRenames(
        Dictionary<string, Hash> oldFiles,
        Dictionary<string, Hash> newFiles,
        List<string> deleted,
        List<string> added)
    {
        var result = new Dictionary<string, (string OldPath, double Similarity)>();
        if (deleted.Count == 0 || added.Count == 0) return result;

        var remainingDeleted = new List<string>(deleted);
        var remainingAdded = new List<string>(added);

        // Pass 1: identical content is unambiguous — a pure move, similarity 1.0.
        foreach (string newPath in added.ToList())
        {
            Hash newHash = newFiles[newPath];
            string? match = remainingDeleted.FirstOrDefault(d => oldFiles[d].Equals(newHash));
            if (match == null) continue;
            result[newPath] = (match, 1.0);
            remainingDeleted.Remove(match);
            remainingAdded.Remove(newPath);
        }

        if (remainingDeleted.Count == 0 || remainingAdded.Count == 0) return result;

        // Pass 2: score every remaining pair and take the best matches greedily.
        var pairs = new List<(double Score, string NewPath, string OldPath)>();
        foreach (string newPath in remainingAdded)
        {
            byte[]? newContent = _repo.Objects.ReadBlob(newFiles[newPath]);
            if (newContent == null || Helpers.IsBinary(newContent)) continue;

            foreach (string oldPath in remainingDeleted)
            {
                byte[]? oldContent = _repo.Objects.ReadBlob(oldFiles[oldPath]);
                if (oldContent == null || Helpers.IsBinary(oldContent)) continue;

                double score = ContentSimilarity(
                    Encoding.UTF8.GetString(oldContent),
                    Encoding.UTF8.GetString(newContent));
                if (score >= RenameSimilarityThreshold)
                    pairs.Add((score, newPath, oldPath));
            }
        }

        var usedNew = new HashSet<string>();
        var usedOld = new HashSet<string>();
        foreach (var (score, newPath, oldPath) in pairs.OrderByDescending(p => p.Score))
        {
            if (usedNew.Contains(newPath) || usedOld.Contains(oldPath)) continue;
            result[newPath] = (oldPath, score);
            usedNew.Add(newPath);
            usedOld.Add(oldPath);
        }

        return result;
    }

    /// <summary>
    /// Line-set similarity in 0..1. Structural rather than textual: it compares the set of
    /// distinct lines, so reformatting does not destroy a rename match the way a byte diff would.
    /// </summary>
    public static double ContentSimilarity(string oldText, string newText)
    {
        if (oldText.Length == 0 && newText.Length == 0) return 1.0;
        if (oldText.Length == 0 || newText.Length == 0) return 0.0;

        var oldLines = new HashSet<string>(SplitLines(oldText));
        var newLines = new HashSet<string>(SplitLines(newText));

        oldLines.Remove("");
        newLines.Remove("");
        if (oldLines.Count == 0 && newLines.Count == 0) return 1.0;
        if (oldLines.Count == 0 || newLines.Count == 0) return 0.0;

        int shared = oldLines.Count(l => newLines.Contains(l));
        int total = oldLines.Count + newLines.Count - shared;
        return total == 0 ? 1.0 : (double)shared / total;
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Diffs the working tree against the INDEX — the not-yet-staged changes.
    /// Comparing the working tree against HEAD instead made a staged change show up as unstaged
    /// and hid the distinction that `diff` vs `diff --cached` is supposed to express.
    /// </summary>
    public List<FileDiff> DiffWorkingTree(Hash commitHash)
    {
        var diffs = new List<FileDiff>();
        var indexedFiles = _repo.Index.GetTrackedFiles();

        foreach (var (path, kind) in _repo.GetStatus().Changes)
        {
            indexedFiles.TryGetValue(path, out Hash oldHash);

            if (kind == ChangeKind.Deleted)
            {
                byte[]? oldContent = _repo.Objects.ReadBlob(oldHash);
                diffs.Add(new FileDiff
                {
                    OldPath = path,
                    NewPath = path,
                    OldHash = oldHash,
                    Kind = ChangeKind.Deleted,
                    IsBinary = oldContent != null && Helpers.IsBinary(oldContent),
                    Hunks = oldContent != null && !Helpers.IsBinary(oldContent)
                        ? ComputeDiff(Encoding.UTF8.GetString(oldContent), "", path)
                        : new()
                });
                continue;
            }

            string fullPath = Path.Combine(_repo.RootPath, path);
            if (!File.Exists(fullPath)) continue;

            byte[] newContent = File.ReadAllBytes(fullPath);
            byte[]? oldContent2 = _repo.Objects.ReadBlob(oldHash);
            bool binary = Helpers.IsBinary(newContent) || (oldContent2 != null && Helpers.IsBinary(oldContent2));

            diffs.Add(new FileDiff
            {
                OldPath = path,
                NewPath = path,
                OldHash = oldHash,
                NewHash = HashUtil.ComputeBlob(newContent),
                Kind = oldHash.Equals(Hash.Zero) ? ChangeKind.Added : ChangeKind.Modified,
                IsBinary = binary,
                Hunks = !binary
                    ? ComputeDiff(oldContent2 != null ? Encoding.UTF8.GetString(oldContent2) : "",
                                  Encoding.UTF8.GetString(newContent), path)
                    : new()
            });
        }

        return diffs.OrderBy(d => d.NewPath, StringComparer.Ordinal).ToList();
    }

    public List<FileDiff> DiffIndex()
    {
        var diffs = new List<FileDiff>();
        Hash? head = _repo.Refs.GetHeadCommit();
        if (!head.HasValue) return diffs;

        Commit? commit = _repo.Objects.ReadCommit(head.Value);
        if (commit == null || !commit.TreeHash.HasValue) return diffs;

        var treeEntries = _repo.GetTreeEntries(commit.TreeHash.Value);
        var indexedFiles = _repo.Index.GetTrackedFiles();

        var allPaths = new HashSet<string>(treeEntries.Keys);
        allPaths.UnionWith(indexedFiles.Keys);

        foreach (string path in allPaths.OrderBy(p => p))
        {
            bool inTree = treeEntries.TryGetValue(path, out Hash treeHash);
            bool inIndex = indexedFiles.TryGetValue(path, out Hash indexHash);

            if (inTree && inIndex && treeHash.Equals(indexHash)) continue;

            byte[]? oldContent = inTree ? _repo.Objects.ReadBlob(treeHash) : null;
            byte[]? newContent = inIndex ? _repo.Objects.ReadBlob(indexHash) : null;

            diffs.Add(new FileDiff
            {
                OldPath = path, NewPath = path,
                OldHash = inTree ? treeHash : null, NewHash = inIndex ? indexHash : null,
                Kind = !inTree ? ChangeKind.Added : !inIndex ? ChangeKind.Deleted : ChangeKind.Modified,
                IsBinary = (newContent != null && Helpers.IsBinary(newContent)) || (oldContent != null && Helpers.IsBinary(oldContent)),
                Hunks = !Helpers.IsBinary(oldContent ?? Array.Empty<byte>()) && !Helpers.IsBinary(newContent ?? Array.Empty<byte>())
                    ? ComputeDiff(oldContent != null ? Encoding.UTF8.GetString(oldContent) : "",
                                   newContent != null ? Encoding.UTF8.GetString(newContent) : "", path)
                    : new List<DiffHunk>()
            });
        }

        return diffs;
    }

    public List<DiffHunk> ComputeDiff(string oldText, string newText, string filePath)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        if (oldLines.Length > 0 && oldLines[^1] == "" && oldText.EndsWith('\n'))
            oldLines = oldLines[..^1];
        if (newLines.Length > 0 && newLines[^1] == "" && newText.EndsWith('\n'))
            newLines = newLines[..^1];

        var edits = ComputeMyersDiff(oldLines, newLines);
        return BuildHunks(edits, oldLines, newLines, filePath);
    }

    /// <summary>
    /// Myers O(ND) diff. The forward pass snapshots the frontier after every edit-distance step,
    /// and the backward pass walks those snapshots to recover the exact edit script — including
    /// where the matches ("snakes") sit relative to each edit, which is what line numbering
    /// depends on. Reconstructing from only the final frontier produced an empty script; hand
    /// rolling the snake walk produced mis-numbered hunks.
    /// </summary>
    private List<MyersEdit> ComputeMyersDiff(string[] a, string[] b)
    {
        var edits = new List<MyersEdit>();
        int n = a.Length, m = b.Length;

        if (n == 0 && m == 0) return edits;
        if (n == 0) { edits.Add(new MyersEdit(0, 1, m)); return edits; }
        if (m == 0) { edits.Add(new MyersEdit(0, 0, n)); return edits; }

        int max = n + m;
        int offset = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();

        int finalD = -1;
        for (int d = 0; d <= max && finalD < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];
                else
                    x = v[offset + k - 1] + 1;
                int y = x - k;

                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;

                if (x >= n && y >= m) { finalD = d; break; }
            }
            // Snapshot only complete rounds: trace[d] is the frontier BEFORE round d.
            if (finalD < 0) trace.Add((int[])v.Clone());
        }

        if (finalD < 0) return edits;

        // Walk backwards. Invariant at the top of each iteration: (x, y) is the position reached
        // at the end of round d, and vPrev is the frontier from round d-1.
        int px = n, py = m;
        for (int d = finalD; d > 0; d--)
        {
            int[] vPrev = d - 1 < trace.Count ? trace[d - 1] : new int[2 * max + 1];
            int k = px - py;

            int prevK;
            if (k == -d || (k != d && vPrev[offset + k - 1] < vPrev[offset + k + 1]))
                prevK = k + 1;
            else
                prevK = k - 1;

            int prevX = vPrev[offset + prevK];
            int prevY = prevX - prevK;

            // Rewind the snake: matches between (prevX+1..) and (prevY+1..) are unchanged.
            while (px > prevX && py > prevY) { px--; py--; }

            if (px == prevX)
                edits.Add(new MyersEdit(prevX, 1, py - prevY));   // insertion of b[prevY..py)
            else
                edits.Add(new MyersEdit(prevX, 0, px - prevX));   // deletion of a[prevX..px)

            px = prevX;
            py = prevY;
        }

        edits.Reverse();
        return edits;
    }

    /// <summary>
    /// Turns an edit script into hunks.
    ///
    /// The script is replayed ONCE in order, advancing the old and new line positions together,
    /// so every emitted line carries its true line number in both revisions. Earlier code tried
    /// to derive the positions independently from each edit's index, which mis-numbered hunks
    /// and attributed lines to the wrong side.
    /// </summary>
    private List<DiffHunk> BuildHunks(List<MyersEdit> edits, string[] oldLines, string[] newLines, string filePath)
    {
        var hunks = new List<DiffHunk>();
        if (edits.Count == 0) return hunks;

        const int contextLines = 3;

        // 1. Replay the script into a flat, positionally-correct operation list.
        var ops = new List<DiffLine>();
        int oi = 0, ni = 0;

        foreach (var e in edits)
        {
            while (oi < e.Index && oi < oldLines.Length)
            {
                ops.Add(new DiffLine
                {
                    Type = DiffLineType.Context,
                    Text = oldLines[oi],
                    OldLineNumber = oi + 1,
                    NewLineNumber = ni + 1
                });
                oi++; ni++;
            }

            if (e.Type == 0)
            {
                for (int j = 0; j < e.Count && oi < oldLines.Length; j++, oi++)
                {
                    ops.Add(new DiffLine
                    {
                        Type = DiffLineType.Deleted,
                        Text = oldLines[oi],
                        OldLineNumber = oi + 1
                    });
                }
            }
            else
            {
                for (int j = 0; j < e.Count && ni < newLines.Length; j++, ni++)
                {
                    ops.Add(new DiffLine
                    {
                        Type = DiffLineType.Added,
                        Text = newLines[ni],
                        NewLineNumber = ni + 1
                    });
                }
            }
        }

        while (oi < oldLines.Length || ni < newLines.Length)
        {
            ops.Add(new DiffLine
            {
                Type = DiffLineType.Context,
                Text = oi < oldLines.Length ? oldLines[oi] : (ni < newLines.Length ? newLines[ni] : ""),
                OldLineNumber = oi < oldLines.Length ? oi + 1 : 0,
                NewLineNumber = ni < newLines.Length ? ni + 1 : 0
            });
            oi++; ni++;
        }

        // 2. Split the operation list into hunks: each change keeps `contextLines` of context,
        //    and changes closer than 2*context are merged into the same hunk.
        var changeIndices = new List<int>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Type != DiffLineType.Context) changeIndices.Add(i);
        }
        if (changeIndices.Count == 0) return hunks;

        int cursor = 0;
        while (cursor < changeIndices.Count)
        {
            int start = Math.Max(0, changeIndices[cursor] - contextLines);
            int end = changeIndices[cursor];

            while (cursor + 1 < changeIndices.Count &&
                   changeIndices[cursor + 1] - end <= contextLines * 2)
            {
                cursor++;
                end = changeIndices[cursor];
            }
            end = Math.Min(ops.Count - 1, end + contextLines);

            var lines = new List<DiffLine>();
            for (int i = start; i <= end; i++) lines.Add(ops[i]);

            int oldStart = lines.FirstOrDefault(l => l.OldLineNumber > 0)?.OldLineNumber ?? 0;
            int newStart = lines.FirstOrDefault(l => l.NewLineNumber > 0)?.NewLineNumber ?? 0;
            int oldCount = lines.Count(l => l.Type != DiffLineType.Added);
            int newCount = lines.Count(l => l.Type != DiffLineType.Deleted);

            hunks.Add(new DiffHunk
            {
                OldStart = oldStart,
                OldCount = oldCount,
                NewStart = newStart,
                NewCount = newCount,
                Header = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@",
                Lines = lines
            });

            cursor++;
        }

        return hunks;
    }

    /// <summary>
    /// Applies hunks to text.
    ///
    /// Context and deleted lines are VERIFIED against the target text. A hunk whose context does
    /// not match means the patch does not apply here; the old implementation ignored that and
    /// produced a plausible-but-wrong file, which silently lost content during rebase and
    /// cherry-pick.
    /// </summary>
    public string ApplyPatch(string text, List<DiffHunk> hunks)
    {
        string[] originalLines = text.Replace("\r\n", "\n").Split('\n');
        if (originalLines.Length > 0 && originalLines[^1] == "" && text.EndsWith('\n'))
            originalLines = originalLines[..^1];

        var result = new List<string>();
        int currentLine = 0;

        foreach (var hunk in hunks)
        {
            while (currentLine < hunk.OldStart - 1)
            {
                if (currentLine < originalLines.Length)
                    result.Add(originalLines[currentLine]);
                currentLine++;
            }

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case DiffLineType.Context:
                    case DiffLineType.Deleted:
                        if (currentLine >= originalLines.Length)
                        {
                            throw new InvalidOperationException(
                                $"patch context runs past the end of the file at line {currentLine + 1}");
                        }
                        if (!string.Equals(originalLines[currentLine], line.Text, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"patch context does not match at line {currentLine + 1}: " +
                                $"expected '{Truncate(line.Text)}', found '{Truncate(originalLines[currentLine])}'");
                        }

                        if (line.Type == DiffLineType.Context)
                            result.Add(originalLines[currentLine]);
                        currentLine++;
                        break;

                    case DiffLineType.Added:
                        result.Add(line.Text);
                        break;
                }
            }
        }

        while (currentLine < originalLines.Length)
        {
            result.Add(originalLines[currentLine]);
            currentLine++;
        }

        return string.Join('\n', result) + '\n';
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private record struct MyersEdit(int Index, int Type, int Count);
}