using System.Text;

namespace SourceManager;

/// <summary>Frame kinds carried by the native <c>sm://</c> protocol.</summary>
public enum SmFrameType : byte
{
    /// <summary>Client opens a connection by announcing itself.</summary>
    Hello = 1,
    /// <summary>Server accepts (or, when <c>Error</c>, refuses) the handshake.</summary>
    HelloAck = 2,
    /// <summary>A method call: <c>{"id":N,"method":"...","params":{...}}</c>.</summary>
    Request = 3,
    /// <summary>A successful method result, echoing the request id.</summary>
    Response = 4,
    /// <summary>An out-of-band failure, or a refusal during the handshake.</summary>
    Error = 5,
    /// <summary>Graceful teardown.</summary>
    Bye = 6,
    /// <summary>Keep-alive probe.</summary>
    Ping = 7,
    /// <summary>Keep-alive reply.</summary>
    Pong = 8,
}

/// <summary>
/// The SourceManager native wire protocol (the <c>sm://</c> scheme).
///
/// Every message is a binary frame with a fixed 12-byte header followed by a variable
/// payload:
///
/// <code>
///   +--------+---------+-------+-------+-------------------+
///   | magic  | version | type  | flags | payload length    |
///   | 4 bytes| 1 byte  | 1 byte| 2 byte| 4 bytes (big-end) |
///   +--------+---------+-------+-------+-------------------+
///   | payload: UTF-8 JSON, `length` bytes                  |
///   +------------------------------------------------------+
/// </code>
///
/// The magic is the ASCII string <c>SMP1</c>. Carrying the method semantics as JSON in the
/// frame payload lets client and server share one dispatch table
/// (<see cref="SmServer"/>) across the stdio, HTTP and native transports, while the framing,
/// handshake and version negotiation belong exclusively to this protocol.
/// </summary>
public static class SmWireProtocol
{
    /// <summary>Frame magic, ASCII <c>SMP1</c>.</summary>
    public static readonly byte[] Magic = { (byte)'S', (byte)'M', (byte)'P', (byte)'1' };

    /// <summary>Human-readable name of the native protocol.</summary>
    public const string ProtocolName = "sm";

    /// <summary>Version of the native <c>sm://</c> protocol, MAJOR.MINOR.</summary>
    public const string ProtocolVersion = "1.0";
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 0;

    /// <summary>Rendered protocol identity, e.g. <c>sm/1.0</c>.</summary>
    public static string VersionTag => $"{ProtocolName}/{ProtocolVersion}";

    /// <summary>True when the peer's major protocol version matches this build.</summary>
    public static bool IsCompatible(string? remoteVersion)
    {
        if (string.IsNullOrEmpty(remoteVersion)) return false;
        int dot = remoteVersion.IndexOf('.');
        string major = dot < 0 ? remoteVersion : remoteVersion[..dot];
        return int.TryParse(major, out int m) && m == ProtocolMajor;
    }

    /// <summary>Binary framing version (the <c>version</c> byte in every frame header).</summary>
    public const byte FrameVersion = 1;

    public const int HeaderSize = 12;

    /// <summary>Upper bound on a single frame payload, to guard against hostile length fields.</summary>
    public const int MaxFrameSize = 64 * 1024 * 1024;

    public static void WriteFrame(Stream stream, SmFrameType type, string payload)
        => WriteFrame(stream, type, Encoding.UTF8.GetBytes(payload));

    public static void WriteFrame(Stream stream, SmFrameType type, byte[] payload)
    {
        int len = payload.Length;
        byte[] header = new byte[HeaderSize];
        Array.Copy(Magic, 0, header, 0, Magic.Length);
        header[4] = FrameVersion;
        header[5] = (byte)type;
        header[6] = 0; // flags: reserved
        header[7] = 0;
        header[8] = (byte)(len >> 24);
        header[9] = (byte)(len >> 16);
        header[10] = (byte)(len >> 8);
        header[11] = (byte)len;
        stream.Write(header, 0, header.Length);
        if (len > 0) stream.Write(payload, 0, len);
        stream.Flush();
    }

    public static (SmFrameType type, byte[] payload) ReadFrame(Stream stream)
    {
        byte[] header = ReadExactly(stream, HeaderSize);

        for (int i = 0; i < Magic.Length; i++)
        {
            if (header[i] != Magic[i])
                throw new InvalidDataException("bad frame magic: not a SourceManager stream");
        }

        byte version = header[4];
        if (version != FrameVersion)
            throw new InvalidDataException($"unsupported frame version {version}, expected {FrameVersion}");

        var type = (SmFrameType)header[5];
        int len = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
        if (len < 0 || len > MaxFrameSize)
            throw new InvalidDataException($"frame length {len} out of range");

        byte[] payload = len == 0 ? Array.Empty<byte>() : ReadExactly(stream, len);
        return (type, payload);
    }

    public static string ReadFrameString(Stream stream, out SmFrameType type)
    {
        var (t, payload) = ReadFrame(stream);
        type = t;
        return Encoding.UTF8.GetString(payload);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buffer, offset, count - offset);
            if (n <= 0) throw new EndOfStreamException("connection closed mid-frame");
            offset += n;
        }
        return buffer;
    }
}
