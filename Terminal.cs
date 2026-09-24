namespace SourceManager;

public static class Terminal
{
    public enum Color
    {
        Default,
        Red,
        Green,
        Yellow,
        Blue,
        Magenta,
        Cyan,
        White,
        Gray,
        BrightRed,
        BrightGreen,
        BrightYellow,
        BrightBlue,
        BrightMagenta,
        BrightCyan,
        BrightWhite
    }

    private static readonly Dictionary<Color, string> ColorCodes = new()
    {
        [Color.Default] = "\u001b[0m",
        [Color.Red] = "\u001b[31m",
        [Color.Green] = "\u001b[32m",
        [Color.Yellow] = "\u001b[33m",
        [Color.Blue] = "\u001b[34m",
        [Color.Magenta] = "\u001b[35m",
        [Color.Cyan] = "\u001b[36m",
        [Color.White] = "\u001b[37m",
        [Color.Gray] = "\u001b[90m",
        [Color.BrightRed] = "\u001b[91m",
        [Color.BrightGreen] = "\u001b[92m",
        [Color.BrightYellow] = "\u001b[93m",
        [Color.BrightBlue] = "\u001b[94m",
        [Color.BrightMagenta] = "\u001b[95m",
        [Color.BrightCyan] = "\u001b[96m",
        [Color.BrightWhite] = "\u001b[97m"
    };

    public static bool EnableColor { get; set; } = !Console.IsOutputRedirected;

    /// <summary>
    /// The first diagnostic written during the current run, used to fill the message of the
    /// JSON error envelope when a command fails without emitting one itself.
    /// </summary>
    public static string? LastError { get; private set; }

    public static void ResetDiagnostics() => LastError = null;

    /// <summary>
    /// Resolves the <c>color.ui</c> setting. Accepts boolean words and the tri-state
    /// <c>auto</c>/<c>always</c>/<c>never</c>; anything else (including absent) means auto,
    /// which colors only when stdout is a terminal.
    /// </summary>
    public static void ApplyColorSetting(string? value)
    {
        string mode = (value ?? "").Trim().ToLowerInvariant();
        EnableColor = mode switch
        {
            "always" or "true" or "yes" or "on" or "1" => true,
            "never" or "false" or "no" or "off" or "0" => false,
            _ => !Console.IsOutputRedirected
        };
    }

    public static void Write(string text, Color color = Color.Default, bool bold = false)
    {
        if (EnableColor)
        {
            Console.Write(ColorCodes[color]);
            if (bold) Console.Write("\u001b[1m");
        }
        Console.Write(text);
        if (EnableColor)
            Console.Write(ColorCodes[Color.Default]);
    }

    public static void WriteLine(string text, Color color = Color.Default, bool bold = false)
    {
        Write(text, color, bold);
        Console.WriteLine();
    }

    public static void WriteError(string text)
    {
        LastError ??= text;
        var prevColor = Console.ForegroundColor;
        if (!Console.IsOutputRedirected)
            Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(text);
        if (!Console.IsOutputRedirected)
            Console.ForegroundColor = prevColor;
    }

    public static void WriteWarning(string text)
    {
        WriteLine($"warning: {text}", Color.Yellow);
    }

    public static void WriteSuccess(string text)
    {
        WriteLine(text, Color.Green);
    }

    public static string Colorize(string text, Color color, bool bold = false)
    {
        if (!EnableColor) return text;
        string boldCode = bold ? "\u001b[1m" : "";
        return $"{ColorCodes[color]}{boldCode}{text}{ColorCodes[Color.Default]}";
    }

    public static void WriteProgress(string text, int current, int total)
    {
        int barWidth = 40;
        double progress = total > 0 ? (double)current / total : 0;
        int filled = (int)(barWidth * progress);
        string bar = new string('=', filled) + new string(' ', barWidth - filled);
        Console.Write($"\r{text} [{bar}] {current}/{total} ({progress * 100:F1}%)");
    }

    public static void WriteProgressDone()
    {
        Console.WriteLine();
    }
}