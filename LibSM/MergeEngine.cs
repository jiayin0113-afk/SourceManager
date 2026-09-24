using System.Text;

namespace SourceManager;

public class MergeEngine
{
    private readonly Repository _repo;

    public MergeEngine(Repository repo)
    {
        _repo = repo;
    }

    public Hash? FindMergeBase(Hash a, Hash b) => _repo.FindMergeBase(a, b);

    public MergeResult MergeBranches(string branchName, MergeStrategy strategy = MergeStrategy.Recursive,
        bool deferCommit = false)
    {
        Hash? headCommit = _repo.Refs.GetHeadCommit();
        Hash? mergeCommit = _repo.Refs.GetBranch(branchName);

        if (!headCommit.HasValue)
            throw new InvalidOperationException("HEAD does not point to a commit");
        if (!mergeCommit.HasValue)
            throw new InvalidOperationException($"Branch '{branchName}' does not exist");

        return MergeCommits(headCommit.Value, mergeCommit.Value, strategy, $"{branchName}", deferCommit);
    }

    /// <summary>
    /// Merges <paramref name="theirs"/> into <paramref name="ours"/>.
    /// When <paramref name="deferCommit"/> is true the merged tree is written and staged but no
    /// commit is created: HEAD stays where it was and MERGE_HEAD is recorded, so the caller
    /// (--squash / --no-commit) owns the commit decision.
    /// </summary>
    public MergeResult MergeCommits(Hash ours, Hash theirs, MergeStrategy strategy, string mergeLabel,
        bool deferCommit = false)
    {
        var result = new MergeResult();

        Hash? mergeBase = _repo.FindMergeBase(ours, theirs);

        Commit? oursCommit = _repo.Objects.ReadCommit(ours);
        Commit? theirsCommit = _repo.Objects.ReadCommit(theirs);

        if (oursCommit == null || theirsCommit == null)
        {
            result.Success = false;
            result.ErrorMessage = "Could not read commit objects";
            return result;
        }

        if (mergeBase == null)
        {
            result.Success = false;
            result.ErrorMessage = "No common ancestor found";
            return result;
        }

        if (mergeBase.Value.Equals(ours))
        {
            result.IsFastForward = true;
            result.NewHead = theirs;
            return result;
        }
        if (mergeBase.Value.Equals(theirs))
        {
            result.IsAlreadyMerged = true;
            return result;
        }

        if (strategy == MergeStrategy.Ours)
        {
            result.NewHead = ours;
            result.IsAlreadyMerged = true;
            return result;
        }
        if (strategy == MergeStrategy.Theirs)
        {
            result.NewHead = theirs;
            result.IsFastForward = true;
            return result;
        }

        Commit? baseCommit = _repo.Objects.ReadCommit(mergeBase.Value);
        if (baseCommit == null || !baseCommit.TreeHash.HasValue || !oursCommit.TreeHash.HasValue || !theirsCommit.TreeHash.HasValue)
        {
            result.Success = false;
            result.ErrorMessage = "Could not read tree objects";
            return result;
        }

        var baseFiles = _repo.GetTreeEntries(baseCommit.TreeHash.Value);
        var oursFiles = _repo.GetTreeEntries(oursCommit.TreeHash.Value);
        var theirsFiles = _repo.GetTreeEntries(theirsCommit.TreeHash.Value);

        var allPaths = new HashSet<string>(baseFiles.Keys);
        allPaths.UnionWith(oursFiles.Keys);
        allPaths.UnionWith(theirsFiles.Keys);

        string mergeMsg = $"Merge '{mergeLabel}' into {(_repo.Refs.GetCurrentBranch() ?? "HEAD")}";

        bool hasConflicts = false;
        var conflicts = new List<ConflictInfo>();

        using var lockFile = _repo.AcquireLock();

        foreach (string path in allPaths.OrderBy(p => p))
        {
            bool inBase = baseFiles.TryGetValue(path, out Hash baseHash);
            bool inOurs = oursFiles.TryGetValue(path, out Hash oursHash);
            bool inTheirs = theirsFiles.TryGetValue(path, out Hash theirsHash);

            if (!inBase && !inOurs && inTheirs)
            {
                byte[]? newTheirsContent = _repo.Objects.ReadBlob(theirsHash);
                if (newTheirsContent != null)
                {
                    string fullPath = Path.Combine(_repo.RootPath, path);
                    PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllBytes(fullPath, newTheirsContent);
                    _repo.StageFile(path);
                }
                continue;
            }

            if (inBase && inOurs && !inTheirs)
            {
                if (oursHash.Equals(baseHash))
                {
                    string fullPath = Path.Combine(_repo.RootPath, path);
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    _repo.Index.Remove(path);
                }
                continue;
            }

            if (inBase && !inOurs && inTheirs)
            {
                if (theirsHash.Equals(baseHash))
                {
                    continue;
                }
            }

            if (!inBase && inOurs && inTheirs)
            {
                if (oursHash.Equals(theirsHash))
                {
                    byte[]? content = _repo.Objects.ReadBlob(oursHash);
                    if (content != null)
                    {
                        string fullPath = Path.Combine(_repo.RootPath, path);
                        PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                        File.WriteAllBytes(fullPath, content);
                        _repo.StageFile(path);
                    }
                    continue;
                }
            }

            if (inOurs && inTheirs && oursHash.Equals(theirsHash))
            {
                continue;
            }

            if (!inOurs && !inTheirs)
            {
                if (File.Exists(Path.Combine(_repo.RootPath, path)))
                    File.Delete(Path.Combine(_repo.RootPath, path));
                continue;
            }

            if ((!inBase && inOurs && !inTheirs) || (!inOurs && inTheirs && inBase))
            {
                continue;
            }

            if (!inOurs && inTheirs && !inBase)
            {
                byte[]? missingTheirsContent = _repo.Objects.ReadBlob(theirsHash);
                if (missingTheirsContent != null)
                {
                    string fullPath = Path.Combine(_repo.RootPath, path);
                    PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllBytes(fullPath, missingTheirsContent);
                    _repo.StageFile(path);
                }
                continue;
            }

            byte[]? baseContent = inBase ? _repo.Objects.ReadBlob(baseHash) : null;
            byte[]? oursContent = inOurs ? _repo.Objects.ReadBlob(oursHash) : null;
            byte[]? theirsContent = inTheirs ? _repo.Objects.ReadBlob(theirsHash) : null;

            string baseText = baseContent != null ? Encoding.UTF8.GetString(baseContent) : "";
            string oursText = oursContent != null ? Encoding.UTF8.GetString(oursContent) : "";
            string theirsText = theirsContent != null ? Encoding.UTF8.GetString(theirsContent) : "";

            var mergeResult = ThreeWayMerge(baseText, oursText, theirsText, path);

            if (mergeResult.HasConflicts)
            {
                hasConflicts = true;

                string fullPath = Path.Combine(_repo.RootPath, path);
                PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, mergeResult.MergedText);

                conflicts.Add(new ConflictInfo
                {
                    Path = path,
                    Resolution = ConflictResolution.Unresolved,
                    OursContent = oursContent,
                    TheirsContent = theirsContent,
                    BaseContent = baseContent,
                    OursHash = inOurs ? oursHash : null,
                    TheirsHash = inTheirs ? theirsHash : null,
                    BaseHash = inBase ? baseHash : null,
                    HunkCount = mergeResult.ConflictHunkCount
                });
            }
            else
            {
                string fullPath = Path.Combine(_repo.RootPath, path);
                PathUtil.EnsureDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, mergeResult.MergedText);
                _repo.StageFile(path);
            }
        }

        if (hasConflicts)
        {
            _repo.Refs.SetMergeHead(theirs);

            // Persist the conflict set. Without this the conflict existed only in this process,
            // so `status`, `commit` and `reset --merge` could not see that a merge was in
            // progress or which paths were unresolved.
            ConflictState.Save(_repo, conflicts, ours, theirs, mergeBase.Value);

            result.HasConflicts = true;
            result.Conflicts = conflicts;
            result.Success = true;
        }
        else
        {
            Hash treeHash = _repo.WriteTreeFromIndex();
            result.MergedTree = treeHash;
            result.MergeMessage = mergeMsg;

            if (deferCommit)
            {
                // Stage the result and record the second parent, but leave HEAD and the branch
                // ref untouched. The merged content is already on disk and in the index.
                _repo.Refs.SetMergeHead(theirs);
                result.Success = true;
            }
            else
            {
                Hash mergeCommitHash = _repo.CreateCommit(treeHash, new List<Hash> { ours, theirs }, mergeMsg);
                _repo.UpdateHead(mergeCommitHash, $"merge {mergeLabel}: {mergeMsg}");
                result.NewHead = mergeCommitHash;
                result.Success = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Line-based three-way merge (diff3).
    ///
    /// Each side is aligned against the base, then the aligned regions are walked together:
    /// a region changed by only one side is taken from that side, identical changes are taken
    /// once, and only a region where BOTH sides changed the same base lines differently becomes
    /// a conflict.
    ///
    /// The previous line-by-line implementation emitted empty conflict blocks whenever both
    /// sides inserted different lines — an extremely common case — so ordinary merges failed.
    /// </summary>
    public ThreeWayMergeResult ThreeWayMerge(string baseText, string oursText, string theirsText, string filePath)
    {
        var result = new ThreeWayMergeResult();

        if (oursText == theirsText) { result.MergedText = oursText; return result; }
        if (oursText == baseText) { result.MergedText = theirsText; return result; }
        if (theirsText == baseText) { result.MergedText = oursText; return result; }

        var baseLines = ToLines(baseText);
        var oursLines = ToLines(oursText);
        var theirsLines = ToLines(theirsText);

        var oursRegions = AlignRegions(baseLines, oursLines);
        var theirsRegions = AlignRegions(baseLines, theirsLines);

        var merged = new List<string>();
        int b = 0, o = 0, t = 0, oi = 0, ti = 0;

        while (b < baseLines.Length || o < oursLines.Length || t < theirsLines.Length)
        {
            var oursRegion = oi < oursRegions.Count ? oursRegions[oi] : (LineRegion?)null;
            var theirsRegion = ti < theirsRegions.Count ? theirsRegions[ti] : (LineRegion?)null;

            int nextBase = Math.Min(
                oursRegion?.BaseStart ?? int.MaxValue,
                theirsRegion?.BaseStart ?? int.MaxValue);

            // Stable lines: identical on both sides, copy straight through.
            while (b < nextBase && b < baseLines.Length)
            {
                merged.Add(baseLines[b]);
                b++; o++; t++;
            }

            bool oursChanges = oursRegion.HasValue && oursRegion.Value.BaseStart == b;
            bool theirsChanges = theirsRegion.HasValue && theirsRegion.Value.BaseStart == b;

            if (!oursChanges && !theirsChanges)
            {
                // Nothing aligned at this position; drain whatever the sides have left.
                while (o < oursLines.Length) merged.Add(oursLines[o++]);
                while (t < theirsLines.Length) merged.Add(theirsLines[t++]);
                break;
            }

            var oursSlice = oursChanges ? SideSlice(oursLines, oursRegion!.Value) : new List<string>();
            var theirsSlice = theirsChanges ? SideSlice(theirsLines, theirsRegion!.Value) : new List<string>();

            if (oursChanges && theirsChanges)
            {
                bool same = oursSlice.SequenceEqual(theirsSlice, StringComparer.Ordinal);
                bool baseOurs = BaseSlice(baseLines, oursRegion!.Value).SequenceEqual(oursSlice, StringComparer.Ordinal);
                bool baseTheirs = BaseSlice(baseLines, theirsRegion!.Value).SequenceEqual(theirsSlice, StringComparer.Ordinal);

                if (same) merged.AddRange(oursSlice);
                else if (baseOurs) merged.AddRange(theirsSlice);
                else if (baseTheirs) merged.AddRange(oursSlice);
                else
                {
                    result.HasConflicts = true;
                    result.ConflictHunkCount++;
                    merged.Add("<<<<<<< ours");
                    merged.AddRange(oursSlice);
                    merged.Add("=======");
                    merged.AddRange(theirsSlice);
                    merged.Add(">>>>>>> theirs");
                }

                b = Math.Max(oursRegion!.Value.BaseEnd, theirsRegion!.Value.BaseEnd);
                o = oursRegion!.Value.SideEnd;
                t = theirsRegion!.Value.SideEnd;
                oi++; ti++;
            }
            else if (oursChanges)
            {
                merged.AddRange(oursSlice);
                b = oursRegion!.Value.BaseEnd;
                o = oursRegion!.Value.SideEnd;
                oi++;
            }
            else
            {
                merged.AddRange(theirsSlice);
                b = theirsRegion!.Value.BaseEnd;
                t = theirsRegion!.Value.SideEnd;
                ti++;
            }
        }

        result.MergedText = string.Join('\n', merged);
        return result;
    }

    private readonly record struct LineRegion(int BaseStart, int BaseEnd, int SideStart, int SideEnd);

    private static string[] ToLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static List<string> SideSlice(string[] lines, LineRegion region)
    {
        var result = new List<string>();
        for (int i = region.SideStart; i < region.SideEnd && i < lines.Length; i++)
            result.Add(lines[i]);
        return result;
    }

    private static List<string> BaseSlice(string[] lines, LineRegion region)
    {
        var result = new List<string>();
        for (int i = region.BaseStart; i < region.BaseEnd && i < lines.Length; i++)
            result.Add(lines[i]);
        return result;
    }

    /// <summary>
    /// Aligns "side" against "base" with a longest-common-subsequence match and returns each
    /// maximal run of side lines that replaced a run of base lines.
    /// </summary>
    private static List<LineRegion> AlignRegions(string[] baseLines, string[] sideLines)
    {
        var regions = new List<LineRegion>();

        int n = baseLines.Length, m = sideLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = baseLines[i] == sideLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int bi = 0, si = 0;
        int regionBaseStart = -1, regionSideStart = -1;

        void Close(int baseEnd, int sideEnd)
        {
            if (regionBaseStart < 0) return;
            regions.Add(new LineRegion(regionBaseStart, baseEnd, regionSideStart, sideEnd));
            regionBaseStart = -1;
        }

        while (bi < n && si < m)
        {
            if (baseLines[bi] == sideLines[si])
            {
                Close(bi, si);
                bi++;
                si++;
                continue;
            }

            if (regionBaseStart < 0)
            {
                regionBaseStart = bi;
                regionSideStart = si;
            }

            if (lcs[bi + 1, si] >= lcs[bi, si + 1]) bi++;
            else si++;
        }

        if (regionBaseStart < 0 && (bi < n || si < m))
        {
            regionBaseStart = bi;
            regionSideStart = si;
        }
        Close(n, m);

        return regions;
    }
}

public class MergeResult
{
    public bool Success { get; set; }
    public bool IsFastForward { get; set; }
    public bool IsAlreadyMerged { get; set; }
    public bool HasConflicts { get; set; }
    public Hash? NewHead { get; set; }
    /// <summary>Tree of the merged content; set on every successful merge, commit or not.</summary>
    public Hash? MergedTree { get; set; }
    /// <summary>The default merge commit message produced by the engine.</summary>
    public string? MergeMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ConflictInfo> Conflicts { get; set; } = new();
}

public class ThreeWayMergeResult
{
    public string MergedText { get; set; } = string.Empty;
    public bool HasConflicts { get; set; }

    /// <summary>Number of conflicting regions, so callers can report conflict size.</summary>
    public int ConflictHunkCount { get; set; }
}

public class MergeLine
{
    public string Text { get; set; } = string.Empty;
    public MergeLineKind Kind { get; set; }
}

public enum MergeLineKind
{
    Common,
    Ours,
    Theirs,
    ConflictMarker
}
