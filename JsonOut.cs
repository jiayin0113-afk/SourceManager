using System.Text;
using System.Text.Json;

namespace SourceManager;

/// <summary>
/// Machine-readable output layer.
///
/// Every command that reports state can emit a stable JSON document instead of prose. This is
/// the primary interface for agents and CI: text output is for humans and is allowed to change,
/// whereas the JSON documents here carry an explicit schema version and keep field names stable.
///
///   sm status --json
///   sm log --json --max-count 20
///   sm diff --json
///
/// The envelope is always {"sm":1,"command":"...","data":...} so a consumer can detect the
/// document shape without guessing, and errors are {"sm":1,"command":"...","error":"..."}.
/// </summary>
public static class JsonOut
{
    /// <summary>Schema version for every document this class emits.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// True once a document (success or error) has been written this run. The top-level
    /// handler uses it to avoid appending a second envelope after a command already spoke.
    /// </summary>
    public static bool Emitted { get; private set; }

    public static void Reset() => Emitted = false;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// True when the caller asked for JSON. Accepts --json (bool) and --format=json.
    /// </summary>
    public static bool Requested(ParseResult args)
    {
        if (args.GetBoolOption("json")) return true;
        string? format = args.GetOption("format");
        return string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes a successful document envelope.</summary>
    public static void Write(string command, object data)
    {
        Emitted = true;
        var envelope = new Dictionary<string, object?>
        {
            ["sm"] = SchemaVersion,
            ["command"] = command,
            ["data"] = data
        };
        Console.Out.Write(JsonSerializer.Serialize(envelope, Options));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    /// <summary>
    /// Writes an error document. The process exit code still conveys failure; the document
    /// carries the reason so a caller never has to parse stderr prose.
    /// </summary>
    public static void WriteError(string command, string message, string? code = null)
    {
        Emitted = true;
        var envelope = new Dictionary<string, object?>
        {
            ["sm"] = SchemaVersion,
            ["command"] = command,
            ["error"] = message,
            ["code"] = code ?? "error"
        };
        Console.Out.Write(JsonSerializer.Serialize(envelope, Options));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    /// <summary>A single file change in a stable, ordered shape.</summary>
    public static object FileChange(string path, ChangeKind kind, string? oldPath = null)
        => new
        {
            path,
            oldPath,
            status = StatusCode(kind)
        };

    public static string StatusCode(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Modified => "modified",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.TypeChanged => "type_changed",
        ChangeKind.Unmerged => "unmerged",
        ChangeKind.Untracked => "untracked",
        ChangeKind.Ignored => "ignored",
        _ => "unknown"
    };

    /// <summary>Formats an object id as a full hex string.</summary>
    public static string Hex(Hash? hash) => hash?.ToHex() ?? "";

    /// <summary>ISO-8601 timestamp with offset, for signatures.</summary>
    public static string Timestamp(DateTimeOffset when) => when.ToString("yyyy-MM-ddTHH:mm:sszzz");
}
