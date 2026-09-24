using System.Text;

namespace SourceManager;

public class ArgParser
{
    private readonly List<ArgOption> _options = new();
    private readonly List<ArgCommand> _commands = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private string _description = string.Empty;
    private string _usage = string.Empty;
    private string _name = string.Empty;

    public ArgParser(string name, string description)
    {
        _name = name;
        _description = description;
    }

    public ArgOption AddOption(string name, string? shortName = null, string? description = null,
        bool required = false, string? defaultValue = null, ArgOptionType type = ArgOptionType.String,
        bool multiple = false, string? dependsOn = null, string? conflictsWith = null)
    {
        var opt = new ArgOption
        {
            LongName = name,
            ShortName = shortName,
            Description = description ?? "",
            Required = required,
            DefaultValue = defaultValue,
            Type = type,
            Multiple = multiple,
            DependsOn = dependsOn,
            ConflictsWith = conflictsWith
        };
        _options.Add(opt);
        return opt;
    }

    public ArgCommand AddCommand(string name, string description, Action<ArgParser>? configure = null)
    {
        var cmd = new ArgCommand
        {
            Name = name,
            Description = description
        };
        if (configure != null)
        {
            var subParser = new ArgParser(name, description);
            // Subcommands must recognise global options that are declared on the root parser
            // (--json, --no-pager, ...), otherwise `sm status --json` is an unknown option.
            subParser.InheritOptionsFrom(this);
            configure(subParser);
            cmd.Parser = subParser;
        }
        _commands.Add(cmd);
        return cmd;
    }

    /// <summary>
    /// Makes this parser's options visible here as well. A subcommand may still declare the
    /// same name, in which case the subcommand's own definition wins.
    /// </summary>
    private void InheritOptionsFrom(ArgParser parent)
    {
        foreach (var opt in parent._options)
        {
            if (_options.Any(o => o.LongName.Equals(opt.LongName, StringComparison.OrdinalIgnoreCase)))
                continue;
            _options.Add(opt);
        }
    }

    public void AddAlias(string alias, string target)
    {
        _aliases[alias] = target;
    }

    public ParseResult Parse(string[] args)
    {
        var result = new ParseResult();
        SeedDefaults(result);
        var normalizedArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--")
            {
                for (int j = i + 1; j < args.Length; j++)
                    result.PassthroughArgs.Add(args[j]);
                break;
            }

            if (arg.StartsWith("--"))
            {
                string option = arg[2..];
                string? value = null;
                int eqIdx = option.IndexOf('=');
                if (eqIdx >= 0)
                {
                    value = option[(eqIdx + 1)..];
                    option = option[..eqIdx];
                }

                if (option == "help")
                {
                    result.ShowHelp = true;
                    continue;
                }

                var optDef = _options.FirstOrDefault(o => o.LongName.Equals(option, StringComparison.OrdinalIgnoreCase));
                if (optDef == null)
                {
                    result.Errors.Add($"Unknown option: --{option}");
                    continue;
                }

                if (optDef.Type == ArgOptionType.Bool)
                {
                    result.Options[optDef.LongName] = "true";
                }
                else if (value != null)
                {
                    AddOptionValue(result, optDef, value);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    i++;
                    AddOptionValue(result, optDef, args[i]);
                }
                else
                {
                    result.Errors.Add($"Option --{option} requires a value");
                }
            }
            else if (arg.StartsWith("-") && arg.Length > 1 && !arg.StartsWith("--"))
            {
                string flags = arg[1..];
                for (int j = 0; j < flags.Length; j++)
                {
                    char flag = flags[j];
                    var optDef = _options.FirstOrDefault(o => o.ShortName == flag.ToString());
                    if (optDef == null)
                    {
                        result.Errors.Add($"Unknown option: -{flag}");
                        continue;
                    }

                    if (optDef.Type == ArgOptionType.Bool)
                    {
                        AddOptionValue(result, optDef, "true");
                    }
                    else if (j == flags.Length - 1 && i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        i++;
                        AddOptionValue(result, optDef, args[i]);
                    }
                    else if (j == flags.Length - 1)
                    {
                        result.Errors.Add($"Option -{flag} requires a value");
                    }
                    else
                    {
                        result.Errors.Add($"Option -{flag} (non-boolean) cannot be combined with other short options");
                    }
                }
            }
            else if (_commands.Any(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)) ||
                     _aliases.TryGetValue(arg, out _))
            {
                string cmdName = arg;
                if (_aliases.TryGetValue(cmdName, out string? resolved))
                    cmdName = resolved;
                var cmd = _commands.First(c => c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase));
                result.Command = cmd;

                var subArgs = new string[args.Length - i - 1];
                Array.Copy(args, i + 1, subArgs, 0, subArgs.Length);
                if (cmd.Parser != null)
                {
                    var subResult = cmd.Parser.Parse(subArgs);
                    result.SubResult = subResult;
                    // Surface subcommand-level parse errors (unknown option, missing value) to
                    // the caller; previously they were silently discarded and the handler ran
                    // with a half-populated option set.
                    result.Errors.AddRange(subResult.Errors);
                    if (subResult.ShowHelp)
                        result.ShowHelp = true;
                }
                else
                {
                    result.PositionalArgs.AddRange(subArgs);
                }
                break;
            }
            else
            {
                result.PositionalArgs.Add(arg);
            }
        }

        // A bare word that matches no command is an unknown command, not a stray argument.
        // Silently printing help and exiting 0 made every typo look like success.
        if (result.Command == null && _commands.Count > 0 && result.PositionalArgs.Count > 0)
            result.UnknownCommand = result.PositionalArgs[0];

        ValidateConstraints(result);

        return result;
    }

    /// <summary>
    /// Records every declared default so GetOption/GetIntOption can fall back to it.
    /// Defaults deliberately do NOT land in Options: HasOption must keep meaning
    /// "the user actually passed this flag".
    /// </summary>
    private void SeedDefaults(ParseResult result)
    {
        foreach (var opt in _options)
        {
            if (opt.DefaultValue != null)
                result.Defaults[opt.LongName] = opt.DefaultValue;
        }
    }

    private void AddOptionValue(ParseResult result, ArgOption opt, string value)
    {
        if (opt.Multiple)
        {
            if (!result.OptionLists.ContainsKey(opt.LongName))
                result.OptionLists[opt.LongName] = new List<string>();
            result.OptionLists[opt.LongName].Add(value);
        }
        else
        {
            result.Options[opt.LongName] = value;
        }
    }

    private void ValidateConstraints(ParseResult result)
    {
        var providedOptions = new HashSet<string>(result.Options.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var list in result.OptionLists)
            providedOptions.Add(list.Key);

        foreach (var opt in _options)
        {
            if (opt.Required && !providedOptions.Contains(opt.LongName) && opt.DefaultValue == null)
                result.Errors.Add($"Required option --{opt.LongName} is missing");

            if (opt.DependsOn != null && providedOptions.Contains(opt.LongName) &&
                !providedOptions.Contains(opt.DependsOn))
                result.Errors.Add($"Option --{opt.LongName} requires --{opt.DependsOn}");

            if (opt.ConflictsWith != null && providedOptions.Contains(opt.LongName) &&
                providedOptions.Contains(opt.ConflictsWith))
                result.Errors.Add($"Option --{opt.LongName} conflicts with --{opt.ConflictsWith}");
        }
    }

    public string GetHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_name} - {_description}");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        if (_commands.Count > 0)
            sb.AppendLine($"  sm <command> [options] [arguments]");
        else
            sb.AppendLine($"  sm {_name} [options] [arguments]");
        sb.AppendLine();

        if (_options.Count > 0)
        {
            sb.AppendLine("Options:");
            int maxLen = _options.Max(o => FormatOptionName(o).Length);
            foreach (var opt in _options.OrderBy(o => o.LongName))
            {
                string name = FormatOptionName(opt).PadRight(maxLen + 4);
                sb.Append($"  {name}{opt.Description}");
                if (opt.DefaultValue != null)
                    sb.Append($" (default: {opt.DefaultValue})");
                if (opt.Required)
                    sb.Append(" [required]");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (_commands.Count > 0)
        {
            sb.AppendLine("Commands:");
            int maxLen = _commands.Max(c => c.Name.Length);
            foreach (var cmd in _commands.OrderBy(c => c.Name))
            {
                string name = cmd.Name.PadRight(maxLen + 4);
                sb.AppendLine($"  {name}{cmd.Description}");
            }
            sb.AppendLine();
            sb.AppendLine("Run 'sm <command> --help' for more information on a command.");
        }

        return sb.ToString();
    }

    private string FormatOptionName(ArgOption opt)
    {
        var parts = new List<string>();
        if (opt.ShortName != null)
            parts.Add($"-{opt.ShortName}");
        parts.Add($"--{opt.LongName}");
        if (opt.Type != ArgOptionType.Bool)
        {
            string val = opt.LongName.ToUpperInvariant();
            parts.Add(opt.Type == ArgOptionType.Int ? $"<{val}>" : $"<{val}>");
        }
        return string.Join(", ", parts);
    }

    public IEnumerable<ArgOption> Options => _options;
    public IEnumerable<ArgCommand> Commands => _commands;
}

public enum ArgOptionType
{
    String,
    Int,
    Bool,
    Enum,
    Path,
    List
}

public class ArgOption
{
    public string LongName { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public ArgOptionType Type { get; set; } = ArgOptionType.String;
    public bool Multiple { get; set; }
    public string? DependsOn { get; set; }
    public string? ConflictsWith { get; set; }
}

public class ArgCommand
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ArgParser? Parser { get; set; }
    public Func<ParseResult, int>? Handler { get; set; }
}

public class ParseResult
{
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declared defaults, applied as a fallback by the getters only.</summary>
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> OptionLists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PositionalArgs { get; } = new();
    public List<string> PassthroughArgs { get; } = new();
    public List<string> Errors { get; } = new();
    public ArgCommand? Command { get; set; }
    public ParseResult? SubResult { get; set; }
    public bool ShowHelp { get; set; }

    /// <summary>Set when the first positional argument matched no known command.</summary>
    public string? UnknownCommand { get; set; }

    public string? GetOption(string name)
    {
        if (Options.TryGetValue(name, out string? value)) return value;
        return Defaults.TryGetValue(name, out string? fallback) ? fallback : null;
    }

    /// <summary>True only when the user explicitly supplied the option.</summary>
    public bool WasProvided(string name) => Options.ContainsKey(name);

    public bool HasOption(string name) => Options.ContainsKey(name);

    public bool GetBoolOption(string name)
    {
        if (HasOption(name)) return Options[name] == "true";
        return Defaults.TryGetValue(name, out string? d) && string.Equals(d, "true", StringComparison.OrdinalIgnoreCase);
    }

    public int? GetIntOption(string name)
    {
        string? raw = GetOption(name);
        return raw != null && int.TryParse(raw, out int v) ? v : null;
    }

    public string? GetPositional(int index)
    {
        // Guard the low end as well: callers pass -1 as a "no such argument" sentinel, and
        // `-1 < Count` is true, so the old check let it reach PositionalArgs[-1].
        return index >= 0 && index < PositionalArgs.Count ? PositionalArgs[index] : null;
    }

    /// <summary>Positional argument by index; null when absent.</summary>
    public string? Positional(int index) => GetPositional(index);
}