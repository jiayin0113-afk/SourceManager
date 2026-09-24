using System.Text;
using System.Text.Json;

namespace SourceManager;

/// <summary>
/// Persists an in-progress conflict set.
///
/// A conflict is structured state, not just marker text in a file: it names the three versions
/// of each path (base / ours / theirs) and their object ids. Keeping that on disk lets any
/// command — `status`, `commit`, `reset --merge`, and any external tool — see exactly what is
/// unresolved and resolve it programmatically instead of pattern-matching conflict markers.
///
/// Stored at .sm/CONFLICTS as JSON.
/// </summary>
public static class ConflictState
{
    private const string FileName = "CONFLICTS";

    private sealed record PersistedConflict(
        string Path,
        string Resolution,
        string? BaseId,
        string? OursId,
        string? TheirsId,
        int Hunks);

    public static string PathFor(Repository repo) => Path.Combine(repo.SmPath, FileName);

    public static bool Exists(Repository repo) => File.Exists(PathFor(repo));

    public static void Save(Repository repo, List<ConflictInfo> conflicts, Hash ours, Hash theirs, Hash mergeBase)
    {
        var payload = new
        {
            ours = ours.ToHex(),
            theirs = theirs.ToHex(),
            mergeBase = mergeBase.ToHex(),
            createdAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            conflicts = conflicts.Select(c => new PersistedConflict(
                c.Path,
                c.Resolution.ToString(),
                c.BaseHash?.ToHex(),
                c.OursHash?.ToHex(),
                c.TheirsHash?.ToHex(),
                c.HunkCount)).ToList()
        };

        var dir = Path.GetDirectoryName(PathFor(repo));
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(PathFor(repo), JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The paths still needing resolution.
    ///
    /// A path counts as resolved when the index now holds content that differs from all three
    /// recorded versions (base, ours, theirs) — that is what `sm add` produces once the user has
    /// edited the file. Inferring this from the index is what makes "edit, add, commit" work
    /// without a separate `mark-resolved` step.
    /// </summary>
    public static List<string> GetUnresolved(Repository repo)
    {
        var unresolved = new List<string>();
        foreach (var conflict in Load(repo))
        {
            if (conflict.Resolution != ConflictResolution.Unresolved) continue;

            IndexEntry? entry = repo.Index.GetEntry(conflict.Path);
            if (entry != null && !MatchesAnyVersion(entry.ObjectHash, conflict))
                continue;

            unresolved.Add(conflict.Path);
        }
        return unresolved;
    }

    private static bool MatchesAnyVersion(Hash staged, ConflictInfo conflict)
        => (conflict.OursHash.HasValue && staged.Equals(conflict.OursHash.Value))
        || (conflict.TheirsHash.HasValue && staged.Equals(conflict.TheirsHash.Value))
        || (conflict.BaseHash.HasValue && staged.Equals(conflict.BaseHash.Value));

    /// <summary>
    /// Loads full conflict records (with the three versions re-read from the object store)
    /// for programmatic resolution.
    /// </summary>
    public static List<ConflictInfo> Load(Repository repo)
    {
        var result = new List<ConflictInfo>();
        if (!File.Exists(PathFor(repo))) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(PathFor(repo)));
            if (!doc.RootElement.TryGetProperty("conflicts", out var arr)) return result;

            foreach (var elem in arr.EnumerateArray())
            {
                string path = GetString(elem, "Path") ?? GetString(elem, "path") ?? "";
                if (path.Length == 0) continue;

                Hash? baseHash = ParseId(elem, "BaseId");
                Hash? oursHash = ParseId(elem, "OursId");
                Hash? theirsHash = ParseId(elem, "TheirsId");

                var info = new ConflictInfo
                {
                    Path = path,
                    BaseHash = baseHash,
                    OursHash = oursHash,
                    TheirsHash = theirsHash,
                    BaseContent = baseHash.HasValue ? repo.Objects.ReadBlob(baseHash.Value) : null,
                    OursContent = oursHash.HasValue ? repo.Objects.ReadBlob(oursHash.Value) : null,
                    TheirsContent = theirsHash.HasValue ? repo.Objects.ReadBlob(theirsHash.Value) : null
                };
                if (TryGetInt(elem, "Hunks", out int hunks))
                    info.HunkCount = hunks;
                string? resolution = GetString(elem, "Resolution");
                if (resolution != null && Enum.TryParse(resolution, true, out ConflictResolution parsed))
                    info.Resolution = parsed;
                result.Add(info);
            }
        }
        catch
        {
            // A malformed file must not break status; report nothing rather than throwing.
        }

        return result;
    }

    private static IEnumerable<string> ReadPaths(Repository repo)
    {
        foreach (var c in Load(repo))
        {
            if (c.Resolution == ConflictResolution.Unresolved)
                yield return c.Path;
        }
    }
    private static Hash? ParseId(JsonElement elem, string name)
    {
        string? hex = GetString(elem, name);
        return hex != null && hex.Length == 64 ? Hash.Parse(hex) : null;
    }

    /// <summary>Case-insensitive property lookup: the persisted casing is not part of the contract.</summary>
    private static string? GetString(JsonElement elem, string name)
    {
        if (elem.TryGetProperty(name, out var exact)) return exact.GetString();
        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.GetString();
        }
        return null;
    }

    private static bool TryGetInt(JsonElement elem, string name, out int value)
    {
        value = 0;
        if (elem.TryGetProperty(name, out var exact) && exact.TryGetInt32(out value)) return true;
        foreach (var prop in elem.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetInt32(out value))
                return true;
        }
        return false;
    }

    /// <summary>Marks a path resolved and drops it from the conflict set when none remain.</summary>
    public static void MarkResolved(Repository repo, string path)
    {
        var conflicts = Load(repo);
        bool changed = false;
        foreach (var c in conflicts)
        {
            if (c.Path == path && c.Resolution == ConflictResolution.Unresolved)
            {
                c.Resolution = ConflictResolution.Manual;
                changed = true;
            }
        }
        if (!changed) return;

        if (conflicts.All(c => c.Resolution != ConflictResolution.Unresolved))
        {
            Clear(repo);
            return;
        }

        var remaining = conflicts.Where(c => c.Resolution == ConflictResolution.Unresolved).ToList();
        var ours = repo.Refs.GetHeadCommit() ?? Hash.Zero;
        var theirs = repo.Refs.GetMergeHead() ?? Hash.Zero;
        Save(repo, remaining, ours, theirs, Hash.Zero);
    }

    public static void Clear(Repository repo)
    {
        string p = PathFor(repo);
        if (File.Exists(p)) File.Delete(p);
    }
}
