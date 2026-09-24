using System.ComponentModel;
using System.Diagnostics;

namespace SourceManager;

/// <summary>
/// Repository hooks. A hook is any file in <c>&lt;sm-dir&gt;/hooks/</c> named after a hook
/// point. It runs with the working tree as its current directory, inherits the console, and
/// can abort the operation it is attached to with a non-zero exit status.
///
/// Supported hook points:
///   pre-commit   no arguments; a non-zero exit aborts the commit
///   commit-msg   one argument, the path to a file holding the message; may rewrite it;
///                a non-zero exit aborts the commit
///   post-commit  no arguments; runs after the commit is recorded (failure does not abort)
///   pre-merge    one argument, the branch being merged; a non-zero exit aborts the merge
///   post-merge   one argument, the branch that was merged (failure does not abort)
///   pre-push     two arguments, the remote name and URL; a non-zero exit aborts the push
///
/// A hook is executed directly when it is a native executable, otherwise an interpreter is
/// chosen from its shebang (<c>#!/bin/sh</c>) or extension (<c>.sh</c>, <c>.ps1</c>, <c>.py</c>,
/// <c>.bat</c>, <c>.cmd</c>). Set <c>SM_NO_HOOKS=1</c> to disable hook execution entirely.
/// </summary>
public static class Hooks
{
    private static bool Disabled => Environment.GetEnvironmentVariable("SM_NO_HOOKS") == "1";

    // A hook may be the bare name (Git-style) or carry an extension that selects an
    // interpreter. Resolution order is deliberate: exact name first, then the common scripts.
    private static readonly string[] HookExtensions =
        { "", ".exe", ".cmd", ".bat", ".ps1", ".sh", ".bash", ".py", ".js" };

    /// <summary>The path of the hook file if present, trying the bare name and known extensions.</summary>
    public static string? Resolve(Repository repo, string name)
    {
        string dir = System.IO.Path.Combine(repo.SmPath, "hooks");
        foreach (string ext in HookExtensions)
        {
            string candidate = System.IO.Path.Combine(dir, name + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool Exists(Repository repo, string name) => Resolve(repo, name) != null;

    /// <summary>
    /// Runs a hook. Returns true when the hook is absent, disabled, succeeds, or when no
    /// interpreter could be found (a missing interpreter must not silently abort work).
    /// Returns false only when the hook actually ran and exited non-zero.
    /// </summary>
    public static bool Run(Repository repo, string name, IReadOnlyList<string>? args = null,
        bool abortOnFailure = true)
    {
        if (Disabled) return true;

        string? hookPath = Resolve(repo, name);
        if (hookPath == null) return true;

        int exitCode = -1;
        bool started = false;

        foreach (var (file, hookArgs) in Candidates(hookPath, args))
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                WorkingDirectory = repo.RootPath,
                UseShellExecute = false
            };
            foreach (string a in hookArgs) psi.ArgumentList.Add(a);
            psi.Environment["SM_DIR"] = repo.SmPath;
            psi.Environment["SM_REPO"] = repo.RootPath;

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                started = true;
                proc.WaitForExit();
                exitCode = proc.ExitCode;
                break;
            }
            catch (Win32Exception)
            {
                // The interpreter is not on PATH; fall through to the next candidate.
            }
            catch (Exception ex)
            {
                SmLog.WriteError($"error: failed to run hook '{name}': {ex.Message}");
                return abortOnFailure ? false : true;
            }
        }

        if (!started)
        {
            SmLog.WriteWarning(
                $"cannot run hook '{name}': no suitable interpreter found " +
                "(add a shebang or a known extension, or make it executable)");
            return true;
        }

        if (exitCode == 0) return true;

        string message = $"hook '{name}' exited with status {exitCode}";
        if (abortOnFailure)
        {
            SmLog.WriteError($"error: {message}");
            return false;
        }
        SmLog.WriteWarning(message);
        return false;
    }

    private static IEnumerable<(string file, List<string> args)> Candidates(
        string hookPath, IReadOnlyList<string>? extra)
    {
        var tail = extra?.ToList() ?? new List<string>();

        string? shebang = ReadShebang(hookPath);
        if (shebang != null)
        {
            (string interp, string? rest) = SplitFirstToken(shebang);
            if (!string.IsNullOrEmpty(interp))
            {
                var args = new List<string>();
                if (!string.IsNullOrEmpty(rest)) args.Add(rest);
                args.Add(hookPath);
                args.AddRange(tail);
                yield return (interp, args);
                yield break;
            }
        }

        string ext = System.IO.Path.GetExtension(hookPath).ToLowerInvariant();
        switch (ext)
        {
            case ".sh":
                yield return ("sh", Args(tail, hookPath));
                yield break;
            case ".bash":
                yield return ("bash", Args(tail, hookPath));
                yield break;
            case ".py":
                yield return ("python3", Args(tail, hookPath));
                yield return ("python", Args(tail, hookPath));
                yield break;
            case ".js":
                yield return ("node", Args(tail, hookPath));
                yield break;
            case ".ps1":
                yield return ("pwsh", Args(tail, "-NoProfile", "-File", hookPath));
                yield return ("powershell", Args(tail, "-NoProfile", "-File", hookPath));
                yield break;
            case ".bat":
            case ".cmd":
                yield return ("cmd", Args(tail, "/c", hookPath));
                yield break;
            case ".exe":
                yield return (hookPath, tail);
                yield break;
            default:
                // Native binary, or a script whose interpreter we must guess.
                yield return (hookPath, tail);
                yield return ("sh", Args(tail, hookPath));
                yield return ("bash", Args(tail, hookPath));
                yield break;
        }
    }

    private static List<string> Args(List<string> tail, params string[] lead)
    {
        var list = new List<string>(lead);
        list.AddRange(tail);
        return list;
    }

    private static string? ReadShebang(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            string? line = reader.ReadLine();
            if (line != null && line.StartsWith("#!"))
                return line[2..].Trim();
        }
        catch
        {
            // Unreadable as text (e.g. a binary); treat as having no shebang.
        }
        return null;
    }

    private static (string interp, string? rest) SplitFirstToken(string text)
    {
        int space = text.IndexOfAny(new[] { ' ', '\t' });
        if (space < 0) return (text, null);
        return (text[..space], text[(space + 1)..].Trim());
    }
}
