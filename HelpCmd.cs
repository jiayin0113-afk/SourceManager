namespace SourceManager;

/// <summary>
/// `sm help [command]` and `sm <command> --help`.
///
/// Without this, `sm help status` was parsed as the unknown positional "help" followed by the
/// `status` command, so asking for help actually RAN the command.
/// </summary>
public static class HelpCmd
{
    public static int Execute(ParseResult args)
    {
        string? topic = args.PositionalArgs.Count > 0 ? args.PositionalArgs[0] : null;

        if (topic == null)
        {
            Program.PrintHelp(null);
            return 0;
        }

        var command = Program.FindCommand(topic);
        if (command == null)
        {
            Terminal.WriteError($"sm: no help for '{topic}': not a SourceManager command");
            return 1;
        }

        if (command.Parser != null)
            Console.WriteLine(command.Parser.GetHelpText());
        else
            Console.WriteLine($"{command.Name} - {command.Description}");
        return 0;
    }
}
