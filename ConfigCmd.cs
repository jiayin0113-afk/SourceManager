namespace SourceManager;

public static class ConfigCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool global = args.GetBoolOption("global");
        bool local = args.GetBoolOption("local");
        bool list = args.GetBoolOption("list");
        bool get = args.GetBoolOption("get");
        bool unset = args.GetBoolOption("unset");
        bool add = args.GetBoolOption("add");
        bool getAll = args.GetBoolOption("get-all");
        string? type = args.GetOption("type");
        bool nullTerminated = args.GetBoolOption("null");

        var positional = args.PositionalArgs;

        if (list)
        {
            foreach (string section in repo.Config.GetSections())
            {
                foreach (var (key, value) in repo.Config.GetSection(section))
                {
                    string line = $"{section}.{key}={value}";
                    if (nullTerminated)
                        Console.Write(line + '\0');
                    else
                        Console.WriteLine(line);
                }
            }
            return 0;
        }

        if (getAll)
        {
            if (positional.Count < 1)
            {
                Terminal.WriteError("error: key must be provided for --get-all");
                return 1;
            }
            string fullKey = positional[0];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];
            foreach (var (k, v) in repo.Config.GetSection(section))
            {
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    if (nullTerminated)
                        Console.Write(v + '\0');
                    else
                        Console.WriteLine(v);
                }
            }
            return 0;
        }

        if (get)
        {
            if (positional.Count < 1)
            {
                Terminal.WriteError("error: key must be provided for --get");
                return 1;
            }
            string fullKey = positional[0];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];
            string? value = repo.Config.Get(section, key);
            if (value == null)
            {
                return 1;
            }

            if (nullTerminated)
                Console.Write(value + '\0');
            else
                Console.WriteLine(value);
            return 0;
        }

        if (unset)
        {
            if (positional.Count < 1)
            {
                Terminal.WriteError("error: key must be provided for --unset");
                return 1;
            }
            string fullKey = positional[0];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];
            repo.Config.Unset(section, key);
            return 0;
        }

        if (add)
        {
            if (positional.Count < 2)
            {
                Terminal.WriteError("error: key and value must be provided for --add");
                return 1;
            }
            string fullKey = positional[0];
            string value = positional[1];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];
            string? existing = repo.Config.Get(section, key);
            if (existing != null)
            {
                repo.Config.Set(section, key, existing + "\n" + value);
            }
            else
            {
                repo.Config.Set(section, key, value);
            }
            return 0;
        }

        if (positional.Count == 1)
        {
            string fullKey = positional[0];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];
            string? value = repo.Config.Get(section, key);
            if (value != null)
            {
                Console.WriteLine(value);
            }
            return 0;
        }
        else if (positional.Count >= 2)
        {
            string fullKey = positional[0];
            string value = positional[1];
            int dotIdx = fullKey.LastIndexOf('.');
            if (dotIdx < 0)
            {
                Terminal.WriteError("error: invalid key format, expected section.key");
                return 1;
            }
            string section = fullKey[..dotIdx];
            string key = fullKey[(dotIdx + 1)..];

            if (type != null)
            {
                switch (type.ToLowerInvariant())
                {
                    case "bool":
                        value = value.ToLowerInvariant() switch
                        {
                            "true" or "yes" or "1" or "on" => "true",
                            _ => "false"
                        };
                        break;
                    case "int":
                        if (!int.TryParse(value, out _))
                        {
                            Terminal.WriteError($"error: invalid int value: {value}");
                            return 1;
                        }
                        break;
                }
            }

            repo.Config.Set(section, key, value);
            return 0;
        }

        Terminal.WriteError("error: no action specified");
        return 1;
    }
}