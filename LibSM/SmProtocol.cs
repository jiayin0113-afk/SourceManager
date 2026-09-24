namespace SourceManager;

/// <summary>
/// The SourceManager transport protocol.
///
/// The canonical carrier is the native <c>sm://</c> protocol: a TCP connection that opens with
/// a binary-framed handshake and then exchanges request/response frames (see
/// <see cref="SmWireProtocol"/>). Two compatibility carriers speak the same request/response
/// model:
///
///   HTTP   POST/GET &lt;base&gt;/sm/... with a JSON body, bearer-token authenticated.
///   stdio  one JSON object per line over stdin/stdout (used by `ssh://` and `sm serve --stdio`).
///
/// Framing on stdio is newline-delimited JSON with three rules that make the stream
/// self-describing:
///
///   1. The first line the server emits is a hello NOTIFICATION (no "id"): status, protocol
///      version and the capability list.
///   2. Every subsequent client message MUST carry a numeric "id"; the server echoes it on the
///      matching response. A response without an id is a notification and is ignored by clients.
///   3. Blank lines are padding/probes and are never answered. Replying to one used to insert
///      an extra message on the stream and shift every later response by one.
///
/// Version negotiation: clients send `hello` with their version, and the server refuses a major
/// version mismatch instead of failing later in a confusing way. Minor differences are compatible
/// and the server's capability list tells the client what it may use.
/// </summary>
public static class SmProtocol
{
    /// <summary>Protocol version this build speaks. MAJOR.MINOR.</summary>
    public const string Version = "1.0";

    public const int MajorVersion = 1;
    public const int MinorVersion = 0;

    /// <summary>Methods a server may advertise, and a client may rely on when present.</summary>
    public static readonly string[] KnownCapabilities =
    {
        "refs",           // read/write branch, tag and remote refs
        "objects",        // read/write objects, batch upload
        "collect-objects",// negotiate a transfer by wants/haves
        "merge-base",     // ancestry queries
        "ancestor",
        "pack-batch"      // multiple objects per round trip
    };

    public static bool IsCompatible(string? remoteVersion)
    {
        if (string.IsNullOrEmpty(remoteVersion)) return false;
        int dot = remoteVersion.IndexOf('.');
        string major = dot < 0 ? remoteVersion : remoteVersion[..dot];
        return int.TryParse(major, out int m) && m == MajorVersion;
    }

    public static string ParseMajor(string version)
    {
        int dot = version.IndexOf('.');
        return dot < 0 ? version : version[..dot];
    }
}
