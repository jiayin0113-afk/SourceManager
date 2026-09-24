using System.Text;
using System.Text.RegularExpressions;

namespace SourceManager;

/// <summary>
/// Applies ignore rules the way a repository user expects:
///
///   * rules come from every <c>.smignore</c> in the tree, not just the root, and a nested file's
///     patterns are relative to the directory that holds it;
///   * <c>.sm/info/exclude</c> is honoured (it was created by init and then never read);
///   * a global excludes file is honoured when configured;
///   * patterns containing a slash are anchored to their ignore file, those without match at any
///     depth;
///   * <c>[abc]</c>, <c>?</c>, <c>*</c> and <c>**</c> behave as globs;
///   * later rules win, including negations, so <c>!keep.txt</c> can re-include a file.
/// </summary>
public class IgnoreMatcher
{
    private readonly List<IgnoreRule> _rules = new();
    private readonly string _rootPath;

    public IgnoreMatcher(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <summary>Reloads every rule source. Cheap enough to call per command.</summary>
    public void LoadRules()
    {
        _rules.Clear();

        // Lowest precedence first: the last matching rule decides.
        LoadGlobalExcludes();
        LoadFile(Path.Combine(_rootPath, "info", "exclude"), "");
        LoadFile(Path.Combine(_rootPath, ".sm", "info", "exclude"), "");

        // Root ignore file, then nested ones in path order so a nested rule overrides its parent.
        LoadFile(Path.Combine(_rootPath, ".smignore"), "");

        foreach (string dir in EnumerateSubdirectories(_rootPath))
        {
            string relative = PathUtil.GetRelativePath(_rootPath, dir);
            LoadFile(Path.Combine(dir, ".smignore"), relative);
        }
    }

    private void LoadGlobalExcludes()
    {
        string? configured = Environment.GetEnvironmentVariable("SM_GLOBAL_EXCLUDES");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            LoadFile(configured, "");
            return;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return;
        string defaultPath = Path.Combine(home, ".sm", "ignore");
        if (File.Exists(defaultPath)) LoadFile(defaultPath, "");
    }

    private void LoadFile(string path, string baseDirectory)
    {
        if (!File.Exists(path)) return;

        foreach (string raw in File.ReadAllLines(path))
        {
            // Trailing spaces are ignored unless escaped; a leading # is a comment.
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            bool negate = line.StartsWith('!');
            if (negate) line = line[1..];

            // "\#" and "\!" are literal characters.
            if (line.StartsWith('\\') && line.Length > 1 && (line[1] == '#' || line[1] == '!'))
                line = line[1..];

            if (line.Length == 0) continue;
            _rules.Add(new IgnoreRule(line, negate, baseDirectory));
        }
    }

    private static IEnumerable<string> EnumerateSubdirectories(string root)
    {
        var stack = new Stack<string>();
        foreach (string d in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(d) == ".sm") continue;
            stack.Push(d);
        }

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            yield return dir;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Path.GetFileName(sub) == ".sm") continue;
                stack.Push(sub);
            }
        }
    }

    /// <summary>True when the path is excluded by the active rules.</summary>
    public bool IsIgnored(string path, bool isDirectory)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        bool ignored = false;

        foreach (var rule in _rules)
        {
            if (rule.Match(normalized, isDirectory))
                ignored = !rule.Negate;
        }

        return ignored;
    }

    private sealed class IgnoreRule
    {
        private readonly Regex _regex;
        private readonly string _baseDirectory;

        public bool Negate { get; }

        public IgnoreRule(string pattern, bool negate, string baseDirectory)
        {
            Negate = negate;
            _baseDirectory = baseDirectory;
            _regex = BuildRegex(pattern);
        }

        public bool Match(string path, bool isDirectory)
        {
            // A nested ignore file only governs paths beneath its own directory.
            if (_baseDirectory.Length > 0)
            {
                string prefix = _baseDirectory + "/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
                path = path[prefix.Length..];
            }

            return _regex.IsMatch(path);
        }

        private static Regex BuildRegex(string pattern)
        {
            bool directoryOnly = pattern.EndsWith('/');
            if (directoryOnly) pattern = pattern[..^1];

            // A slash anywhere except a trailing position anchors the pattern to the ignore
            // file's directory; otherwise it matches at any depth.
            bool anchored = pattern.StartsWith('/') || pattern.TrimStart('/').Contains('/');
            if (pattern.StartsWith('/')) pattern = pattern[1..];

            var sb = new StringBuilder();
            sb.Append('^');
            if (!anchored) sb.Append("(?:.*/)?");

            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                switch (c)
                {
                    case '*':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            // "**" crosses directory boundaries.
                            while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                            if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            {
                                i++;
                                sb.Append("(?:.*/)?");
                            }
                            else
                            {
                                sb.Append(".*");
                            }
                        }
                        else
                        {
                            sb.Append("[^/]*");
                        }
                        break;

                    case '?':
                        sb.Append("[^/]");
                        break;

                    case '[':
                    {
                        // A character class passes through verbatim so [abc] and [a-z] work;
                        // only an unclosed '[' is treated as a literal.
                        int close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\[");
                            break;
                        }

                        string cls = pattern[(i + 1)..close];
                        if (cls.StartsWith('!')) cls = "^" + cls[1..];
                        sb.Append('[').Append(cls).Append(']');
                        i = close;
                        break;
                    }

                    case '.':
                    case '+':
                    case '(':
                    case ')':
                    case '{':
                    case '}':
                    case ']':
                    case '\\':
                    case '|':
                    case '^':
                    case '$':
                        sb.Append('\\').Append(c);
                        break;

                    default:
                        sb.Append(c);
                        break;
                }
            }

            // A directory-only rule matches the directory itself and everything beneath it.
            if (directoryOnly) sb.Append("(?:/.*)?");
            sb.Append('$');

            return new Regex(sb.ToString(), RegexOptions.Compiled);
        }
    }
}
