using System.Text.Json;

namespace SourceManager;

/// <summary>
/// Saves uncommitted work across an operation that rewrites the working tree (rebase, pull
/// --rebase) and puts it back afterwards.
///
/// Unlike a normal stash this is not something the user asked to keep: it exists only to survive
/// one operation, and it must be restored on success, on --continue, and on --abort. Losing it
/// silently is the worst outcome, so the captured set is stored under .sm and re-applied rather
/// than reconstructed.
/// </summary>
public static class Autostash
{
    private const string StateFile = "AUTOSTASH";

    private sealed record Entry(string Path, string? ContentBase64, bool Deleted);

    private static string PathFor(Repository repo) => Path.Combine(repo.SmPath, StateFile);

    public static bool Exists(Repository repo) => File.Exists(PathFor(repo));

    /// <summary>Records the current bytes of every dirty tracked path.</summary>
    public static void Capture(Repository repo, IEnumerable<string> dirtyPaths)
    {
        var entries = new List<Entry>();
        foreach (string path in dirtyPaths.Distinct())
        {
            string full = Path.Combine(repo.RootPath, path);
            if (File.Exists(full))
            {
                entries.Add(new Entry(path, Convert.ToBase64String(File.ReadAllBytes(full)), false));
            }
            else
            {
                entries.Add(new Entry(path, null, true));
            }
        }

        File.WriteAllText(PathFor(repo), JsonSerializer.Serialize(entries,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Puts the captured content back. Returns the number of paths restored.
    /// Never throws: a failed restore must not mask the outcome of the operation itself.
    /// </summary>
    public static int Restore(Repository repo)
    {
        if (!File.Exists(PathFor(repo))) return 0;

        int restored = 0;
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(PathFor(repo)));
            foreach (var entry in entries ?? new List<Entry>())
            {
                string full = Path.Combine(repo.RootPath, entry.Path);
                string? dir = Path.GetDirectoryName(full);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (entry.Deleted)
                {
                    if (File.Exists(full)) File.Delete(full);
                }
                else if (entry.ContentBase64 != null)
                {
                    File.WriteAllBytes(full, Convert.FromBase64String(entry.ContentBase64));
                }
                restored++;
            }
        }
        catch
        {
            // Leave the state file in place so the work is not lost.
            return restored;
        }

        Clear(repo);
        return restored;
    }

    public static void Clear(Repository repo)
    {
        string p = PathFor(repo);
        if (File.Exists(p)) File.Delete(p);
    }
}
