using System.Runtime.InteropServices;

namespace SourceManager;

public static class Helpers
{
    public static string GetDefaultUserName()
    {
        return System.Environment.UserName;
    }

    public static string GetDefaultUserEmail()
    {
        return $"{System.Environment.UserName}@{System.Environment.MachineName}.local";
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string GetExecutablePath()
    {
        return System.Environment.ProcessPath ?? "sm";
    }

    public static void WriteAllBytesAtomic(string path, byte[] data)
    {
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }

    public static void WriteAllTextAtomic(string path, string text)
    {
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    public static bool IsBinary(byte[] data, int checkLength = 8000)
    {
        int len = Math.Min(data.Length, checkLength);
        for (int i = 0; i < len; i++)
        {
            if (data[i] == 0)
                return true;
        }
        return false;
    }

    public static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[8000];
            int read = fs.Read(buffer, 0, buffer.Length);
            return IsBinary(buffer.AsSpan(0, read).ToArray());
        }
        catch
        {
            return true;
        }
    }

    public static string AbbreviateHash(Hash hash, int length = 7)
    {
        return hash.ToHex()[..Math.Min(length, 64)];
    }

    public static Hash ResolvePartialHash(Repository repo, string partial)
    {
        if (partial.Length == 64)
            return Hash.Parse(partial);

        string fullPath = Path.Combine(repo.SmPath, "objects");
        var candidates = new List<Hash>();

        foreach (string dir in Directory.GetDirectories(fullPath))
        {
            string dirName = Path.GetFileName(dir);
            if (!partial.StartsWith(dirName, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string file in Directory.GetFiles(dir))
            {
                string fileName = Path.GetFileName(file);
                string fullHash = dirName + fileName;
                if (fullHash.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Hash.Parse(fullHash));
                }
            }
        }

        if (candidates.Count == 0)
            throw new ArgumentException($"No object found matching '{partial}'");
        if (candidates.Count > 1)
            throw new ArgumentException($"Ambiguous hash '{partial}': {candidates.Count} matches found");
        return candidates[0];
    }

    public static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int order = 0;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    public static string FormatTimeAgo(DateTimeOffset when)
    {
        TimeSpan diff = DateTimeOffset.Now - when;
        if (diff.TotalSeconds < 60) return $"{Math.Floor(diff.TotalSeconds)} seconds ago";
        if (diff.TotalMinutes < 60) return $"{Math.Floor(diff.TotalMinutes)} minutes ago";
        if (diff.TotalHours < 24) return $"{Math.Floor(diff.TotalHours)} hours ago";
        if (diff.TotalDays < 30) return $"{Math.Floor(diff.TotalDays)} days ago";
        if (diff.TotalDays < 365) return $"{Math.Floor(diff.TotalDays / 30)} months ago";
        return $"{Math.Floor(diff.TotalDays / 365)} years ago";
    }

    public static bool TryCreateHardLink(string source, string dest)
    {
        try
        {
            if (File.Exists(dest))
                File.Delete(dest);
            if (IsWindows)
            {
                return CreateHardLink(dest, source, IntPtr.Zero);
            }
            else
            {
                int result = link(source, dest);
                return result == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}