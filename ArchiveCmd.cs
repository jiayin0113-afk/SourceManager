using System.IO.Compression;
using System.Text;

namespace SourceManager;

/// <summary>
/// `sm archive` — export a tree to a zip or tar stream.
///
/// Reads straight from the object database, so it works on a bare or partially checked-out
/// repository and needs no working tree. Output goes to stdout unless --output is given, which
/// makes it composable in a pipeline.
/// </summary>
public static class ArchiveCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        string spec = args.GetPositional(0) ?? "HEAD";
        string format = (args.GetOption("format") ?? "zip").ToLowerInvariant();
        string? outputPath = args.GetOption("output");
        string? prefix = args.GetOption("prefix");

        if (format is not ("zip" or "tar"))
        {
            Terminal.WriteError($"fatal: unsupported archive format '{format}' (expected zip or tar)");
            return 1;
        }

        Hash treeHash;
        string label;
        try
        {
            var target = RevisionResolver.Resolve(repo, spec);
            Commit? commit = repo.Objects.ReadCommit(target.Commit);
            if (commit?.TreeHash.HasValue != true)
            {
                Terminal.WriteError($"fatal: '{spec}' does not point at a commit with a tree");
                return 1;
            }
            treeHash = commit.TreeHash.Value;
            label = target.Commit.Short;
        }
        catch (RevisionException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            return 1;
        }

        var files = repo.GetTreeEntries(treeHash);
        if (files.Count == 0)
        {
            Terminal.WriteWarning("archive is empty (the tree has no files)");
        }

        // Normalise the prefix so callers can pass "project" or "project/" and get the same tree.
        string normalizedPrefix = string.IsNullOrEmpty(prefix)
            ? ""
            : prefix.Replace('\\', '/').TrimEnd('/') + "/";

        Stream output;
        bool ownsOutput = false;
        if (outputPath != null)
        {
            string full = Path.GetFullPath(outputPath, repo.RootPath);
            string? dir = Path.GetDirectoryName(full);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            output = File.Create(full);
            ownsOutput = true;
        }
        else
        {
            output = Console.OpenStandardOutput();
        }

        try
        {
            if (format == "zip")
                WriteZip(repo, files, output, normalizedPrefix);
            else
                WriteTar(repo, files, output, normalizedPrefix);
        }
        finally
        {
            output.Flush();
            if (ownsOutput) output.Dispose();
        }

        if (outputPath != null)
            Terminal.WriteSuccess($"Archived {files.Count} file(s) from {label} to {outputPath}");

        return 0;
    }

    private static void WriteZip(Repository repo, Dictionary<string, Hash> files, Stream output, string prefix)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            byte[]? content = repo.Objects.ReadBlob(hash);
            if (content == null) continue;

            var entry = zip.CreateEntry(prefix + path, CompressionLevel.Optimal);
            // Fixed timestamp: the same tree must produce the same archive bytes, otherwise
            // archives cannot be compared or cached.
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }
    }

    private static void WriteTar(Repository repo, Dictionary<string, Hash> files, Stream output, string prefix)
    {
        foreach (var (path, hash) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            byte[]? content = repo.Objects.ReadBlob(hash);
            if (content == null) continue;
            WriteTarEntry(output, prefix + path, content);
        }

        // Two zero blocks terminate a tar stream.
        output.Write(new byte[1024], 0, 1024);
    }

    private static void WriteTarEntry(Stream output, string name, byte[] content)
    {
        byte[] header = new byte[512];
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 100)
            throw new InvalidOperationException($"path too long for tar: {name}");
        Array.Copy(nameBytes, 0, header, 0, nameBytes.Length);

        // Mode 0644, uid/gid 0, size, mtime 0, typeflag '0' — a deterministic regular file.
        WriteOctal(header, 100, 8, 0b110_100_100);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, content.Length);
        WriteOctal(header, 136, 12, 0);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

        // Checksum: computed with the checksum field treated as spaces.
        for (int i = 148; i < 156; i++) header[i] = (byte)' ';
        int checksum = header.Sum(b => (int)b);
        WriteOctal(header, 148, 8, checksum);
        header[155] = (byte)' ';

        output.Write(header, 0, 512);
        output.Write(content, 0, content.Length);

        int padding = (512 - (content.Length % 512)) % 512;
        if (padding > 0) output.Write(new byte[padding], 0, padding);
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0;
    }
}
