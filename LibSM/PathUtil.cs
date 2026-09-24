namespace SourceManager;

public static class PathUtil
{
    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    public static string ResolveRelative(string basePath, string relativePath)
    {
        string combined = Path.Combine(basePath, relativePath);
        return Path.GetFullPath(combined);
    }

    public static bool IsSubPath(string parentPath, string childPath)
    {
        string normParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normChild = Path.GetFullPath(childPath);
        return normChild.StartsWith(normParent, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRelativePath(string basePath, string fullPath)
    {
        string normBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normFull = Path.GetFullPath(fullPath);
        if (!normFull.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path '{fullPath}' is not under base path '{basePath}'");
        string relative = normFull[normBase.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return NormalizePath(relative);
    }

    public static IEnumerable<string> EnumerateAllFiles(string directory, IgnoreMatcher? ignoreMatcher = null)
    {
        var files = new List<string>();
        var dirs = new Stack<string>();
        dirs.Push(directory);
        while (dirs.Count > 0)
        {
            string current = dirs.Pop();
            foreach (string file in Directory.GetFiles(current))
            {
                string rel = GetRelativePath(directory, file);
                if (ignoreMatcher == null || !ignoreMatcher.IsIgnored(rel, false))
                    files.Add(file);
            }
            foreach (string dir in Directory.GetDirectories(current))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName == ".sm" || dirName == ".git" || dirName == ".svn") continue;
                string rel = GetRelativePath(directory, dir);
                if (ignoreMatcher == null || !ignoreMatcher.IsIgnored(rel, true))
                    dirs.Push(dir);
                else
                {
                    foreach (string nestedFile in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string nrel = GetRelativePath(directory, nestedFile);
                        if (ignoreMatcher == null || !ignoreMatcher.IsIgnored(nrel, false))
                            files.Add(nestedFile);
                    }
                }
            }
        }
        return files;
    }

    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    public static bool IsSmDirectory(string path)
    {
        string normalized = NormalizePath(path);
        return normalized == ".sm" || normalized.StartsWith(".sm/");
    }
}