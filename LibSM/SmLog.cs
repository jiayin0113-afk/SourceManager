namespace SourceManager;

/// <summary>
/// Logging seam for LibSM. The library never writes to the console directly — every diagnostic,
/// warning and progress message goes through here. The <c>sm</c> CLI wires these handlers to its
/// terminal renderer; embedders may leave them unset (the library stays silent) or route them to
/// their own logger.
/// </summary>
public static class SmLog
{
    /// <summary>Mirrors the CLI's colour set so call sites read identically in both projects.</summary>
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

    /// <summary>Invoked for error diagnostics. Unset means "stay silent".</summary>
    public static Action<string>? ErrorHandler { get; set; }

    /// <summary>Invoked for warnings. Unset means "stay silent".</summary>
    public static Action<string>? WarningHandler { get; set; }

    /// <summary>Invoked for success messages. Unset means "stay silent".</summary>
    public static Action<string>? SuccessHandler { get; set; }

    /// <summary>Invoked for general output. Unset means "stay silent".</summary>
    public static Action<string, Color, bool>? LineHandler { get; set; }

    public static void WriteError(string text) => ErrorHandler?.Invoke(text);

    public static void WriteWarning(string text) => WarningHandler?.Invoke(text);

    public static void WriteSuccess(string text) => SuccessHandler?.Invoke(text);

    public static void WriteLine(string text, Color color = Color.Default, bool bold = false)
        => LineHandler?.Invoke(text, color, bold);

    public static void Write(string text, Color color = Color.Default, bool bold = false)
        => LineHandler?.Invoke(text, color, bold);
}
