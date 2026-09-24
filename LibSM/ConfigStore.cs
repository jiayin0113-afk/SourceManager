using System.Text;

namespace SourceManager;

public class ConfigStore
{
    private readonly string _configPath;
    private readonly string _globalConfigPath;
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public ConfigStore(string repoPath)
    {
        _configPath = Path.Combine(repoPath, ".sm", "config");
        _globalConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".smconfig");

        Load();
    }

    public void Load()
    {
        _sections.Clear();
        if (File.Exists(_globalConfigPath))
            ParseIni(_globalConfigPath);
        if (File.Exists(_configPath))
            ParseIni(_configPath);
    }

    private void ParseIni(string path)
    {
        string[] lines = File.ReadAllLines(path);
        string currentSection = "";
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();
            if (!string.IsNullOrEmpty(currentSection) && !string.IsNullOrEmpty(key))
            {
                if (!_sections.ContainsKey(currentSection))
                    _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[currentSection][key] = value;
            }
        }
    }

    public void Save()
    {
        PathUtil.EnsureDirectory(Path.GetDirectoryName(_configPath)!);
        var sb = new StringBuilder();
        foreach (var section in _sections.OrderBy(s => s.Key))
        {
            sb.AppendLine($"[{section.Key}]");
            foreach (var kv in section.Value.OrderBy(k => k.Key))
                sb.AppendLine($"\t{kv.Key} = {kv.Value}");
            sb.AppendLine();
        }
        File.WriteAllText(_configPath, sb.ToString());
    }

    public string? Get(string section, string key, string? defaultValue = null)
    {
        section = section.ToLowerInvariant();
        key = key.ToLowerInvariant();
        if (_sections.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var value))
            return value;
        return defaultValue;
    }

    public T Get<T>(string section, string key, T defaultValue) where T : notnull
    {
        string? value = Get(section, key);
        if (value == null) return defaultValue;
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Set(string section, string key, string value)
    {
        section = section.ToLowerInvariant();
        key = key.ToLowerInvariant();
        if (!_sections.ContainsKey(section))
            _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _sections[section][key] = value;
        Save();
    }

    public void Unset(string section, string key)
    {
        section = section.ToLowerInvariant();
        key = key.ToLowerInvariant();
        if (_sections.TryGetValue(section, out var sec))
        {
            sec.Remove(key);
            if (sec.Count == 0)
                _sections.Remove(section);
        }
        Save();
    }

    public IEnumerable<string> GetSections()
    {
        return _sections.Keys.OrderBy(k => k);
    }

    public IEnumerable<(string key, string value)> GetSection(string section)
    {
        section = section.ToLowerInvariant();
        if (_sections.TryGetValue(section, out var sec))
            return sec.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value));
        return Enumerable.Empty<(string, string)>();
    }

    public string GetUserName() => Get("user", "name", Helpers.GetDefaultUserName())!;
    public string GetUserEmail() => Get("user", "email", Helpers.GetDefaultUserEmail())!;
    public string GetDefaultBranch() => Get("init", "defaultbranch", "main")!;
    public bool GetColorUi() => Get("color", "ui", true);
    public DiffAlgorithm GetDiffAlgorithm()
    {
        string? val = Get("diff", "algorithm");
        return val?.ToLowerInvariant() switch
        {
            "patience" => DiffAlgorithm.Patience,
            "histogram" => DiffAlgorithm.Histogram,
            "minimal" => DiffAlgorithm.Minimal,
            "semantic" => DiffAlgorithm.Semantic,
            _ => DiffAlgorithm.Myers
        };
    }
    public MergeStrategy GetMergeStrategy()
    {
        string? val = Get("merge", "strategy");
        return val?.ToLowerInvariant() switch
        {
            "recursive" => MergeStrategy.Recursive,
            "ours" => MergeStrategy.Ours,
            "theirs" => MergeStrategy.Theirs,
            "octopus" => MergeStrategy.Octopus,
            "subtree" => MergeStrategy.Subtree,
            "resolve" => MergeStrategy.Resolve,
            "patience" => MergeStrategy.Patience,
            _ => MergeStrategy.FastForward
        };
    }
}