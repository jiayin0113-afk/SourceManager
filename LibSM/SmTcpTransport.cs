using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SourceManager;

/// <summary>
/// Client transport for the native <c>sm://</c> protocol.
///
/// URL form: <c>sm://[token@]host[:port][/path]</c>. The default port is 9418. An optional
/// token in the user-info position (or <c>SM_TOKEN_&lt;REMOTE&gt;</c> / <c>SM_TOKEN</c>) is
/// presented during the binary handshake and checked by the server against
/// <c>SM_SERVE_TOKEN</c>.
/// </summary>
public class SmTcpTransport : Transport
{
    public const int DefaultPort = 9418;

    private readonly string _host;
    private readonly int _port;
    private readonly string _path;
    private readonly string? _remoteName;
    private readonly string? _urlToken;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _nextRequestId;
    private HashSet<string> _serverCapabilities = new();
    private readonly object _lock = new();

    public SmTcpTransport(string url, string? remoteName)
    {
        _remoteName = remoteName;

        string rest = url.StartsWith("sm://") ? url[5..] : url;

        string? userInfo = null;
        int atIdx = rest.IndexOf('@');
        string hostPart;
        if (atIdx >= 0)
        {
            userInfo = rest[..atIdx];
            hostPart = rest[(atIdx + 1)..];
        }
        else
        {
            hostPart = rest;
        }
        _urlToken = string.IsNullOrEmpty(userInfo) ? null : Uri.UnescapeDataString(userInfo);

        int slashIdx = hostPart.IndexOf('/');
        if (slashIdx >= 0)
        {
            _path = hostPart[(slashIdx + 1)..];
            hostPart = hostPart[..slashIdx];
        }
        else
        {
            _path = ".";
        }

        int colonIdx = hostPart.LastIndexOf(':');
        if (colonIdx > 0)
        {
            _host = hostPart[..colonIdx];
            _port = int.Parse(hostPart[(colonIdx + 1)..]);
        }
        else
        {
            _host = hostPart;
            _port = DefaultPort;
        }

        if (string.IsNullOrEmpty(_host))
            throw new InvalidOperationException($"sm:// URL is missing a host: {url}");
    }

    /// <summary>The repository path component of the URL (informational; the server serves one repo).</summary>
    public string RequestedPath => _path;

    /// <summary>The native protocol version the server advertised, or null before connecting.</summary>
    public string? ServerProtocolVersion { get; private set; }

    private void EnsureConnected()
    {
        lock (_lock)
        {
            if (_client != null && _client.Connected) return;
            Connect();
        }
    }

    private void Connect()
    {
        Disconnect();

        _client = new TcpClient();
        try
        {
            _client.Connect(_host, _port);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"could not connect to sm://{_host}:{_port}: {ex.Message}", ex);
        }
        _stream = _client.GetStream();

        var hello = new Dictionary<string, object?>
        {
            ["protocol"] = SmWireProtocol.ProtocolName,
            ["version"] = SmWireProtocol.ProtocolVersion,
            ["frameVersion"] = SmWireProtocol.FrameVersion,
            ["capabilities"] = SmProtocol.KnownCapabilities
        };
        string? token = ResolveToken();
        if (token != null) hello["token"] = token;

        SmWireProtocol.WriteFrame(_stream, SmFrameType.Hello, TransportJson.Serialize(hello));

        var (type, payload) = SmWireProtocol.ReadFrame(_stream);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
        }
        catch
        {
            throw new InvalidOperationException("server sent a malformed handshake");
        }

        var root = doc.RootElement;
        JsonElement err = default;
        bool hasErrorProp = root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("error", out err) &&
                            err.ValueKind == JsonValueKind.String;
        if (type == SmFrameType.Error || hasErrorProp)
        {
            string message = hasErrorProp ? err.GetString() ?? "" : "handshake refused";
            Disconnect();
            throw new InvalidOperationException($"server rejected the handshake: {message}");
        }
        if (type != SmFrameType.HelloAck)
        {
            Disconnect();
            throw new InvalidOperationException($"unexpected handshake frame: {type}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            Disconnect();
            throw new InvalidOperationException("server sent a malformed handshake");
        }

        string? agreedVersion = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        if (!SmWireProtocol.IsCompatible(agreedVersion))
        {
            Disconnect();
            throw new InvalidOperationException(
                $"protocol version mismatch: server speaks {agreedVersion ?? "unknown"}, " +
                $"this client speaks {SmWireProtocol.ProtocolVersion}");
        }

        if (root.TryGetProperty("frameVersion", out var fv) && fv.TryGetByte(out byte serverFrame) &&
            serverFrame != SmWireProtocol.FrameVersion)
        {
            Disconnect();
            throw new InvalidOperationException(
                $"frame version mismatch: server uses v{serverFrame}, this client uses v{SmWireProtocol.FrameVersion}");
        }

        ServerProtocolVersion = agreedVersion;

        _serverCapabilities = root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
            ? caps.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToHashSet()
            : new HashSet<string>();
    }

    /// <summary>Token for the handshake: URL user-info, else SM_TOKEN_&lt;REMOTE&gt;, else SM_TOKEN.</summary>
    private string? ResolveToken()
    {
        if (!string.IsNullOrEmpty(_urlToken)) return _urlToken;

        if (!string.IsNullOrEmpty(_remoteName))
        {
            string? perRemote = Environment.GetEnvironmentVariable(
                $"SM_TOKEN_{_remoteName.ToUpperInvariant().Replace('-', '_')}");
            if (!string.IsNullOrEmpty(perRemote)) return perRemote;
        }
        string? global = Environment.GetEnvironmentVariable("SM_TOKEN");
        return string.IsNullOrEmpty(global) ? null : global;
    }

    private void Disconnect()
    {
        try { if (_stream != null) SmWireProtocol.WriteFrame(_stream, SmFrameType.Bye, "{}"); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    private JsonDocument? SendRequest(string method, Dictionary<string, object?>? parameters = null)
    {
        lock (_lock)
        {
            EnsureConnected();

            long id = ++_nextRequestId;
            var request = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };

            SmWireProtocol.WriteFrame(_stream!, SmFrameType.Request, TransportJson.Serialize(request));

            for (int guard = 0; guard < 10000; guard++)
            {
                SmFrameType type;
                byte[] payload;
                try
                {
                    (type, payload) = SmWireProtocol.ReadFrame(_stream!);
                }
                catch
                {
                    Disconnect();
                    return null;
                }

                if (type == SmFrameType.Ping)
                {
                    SmWireProtocol.WriteFrame(_stream!, SmFrameType.Pong, "{}");
                    continue;
                }
                if (type == SmFrameType.Bye)
                {
                    Disconnect();
                    return null;
                }
                if (type != SmFrameType.Response && type != SmFrameType.Error)
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload)); }
                catch { continue; }

                if (doc.RootElement.TryGetProperty("id", out var idProp) &&
                    idProp.TryGetInt64(out long gotId) && gotId == id)
                {
                    return doc;
                }
            }

            Disconnect();
            return null;
        }
    }

    private string? GetStringField(JsonDocument? doc, string field)
        => doc?.RootElement.TryGetProperty(field, out var val) == true &&
           val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    private bool GetBoolField(JsonDocument? doc, string field)
        => doc?.RootElement.TryGetProperty(field, out var val) == true &&
           val.ValueKind == JsonValueKind.True;

    private Hash? ParseHashField(JsonDocument? doc, string field)
    {
        string? hex = GetStringField(doc, field);
        if (string.IsNullOrEmpty(hex)) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    // ─── Refs ───────────────────────────────────────────────────────

    public override Hash? GetHeadCommit() => ParseHashField(SendRequest("get-head-commit"), "hash");

    public override string? GetCurrentBranch() => GetStringField(SendRequest("get-current-branch"), "branch");

    public override List<BranchInfo> ListBranches()
    {
        var result = new List<BranchInfo>();
        var doc = SendRequest("list-branches");
        if (doc != null && doc.RootElement.TryGetProperty("branches", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in arr.EnumerateArray())
                result.Add(BranchInfo.FromJson(elem.GetRawText()));
        }
        return result;
    }

    public override Hash? GetBranch(string branchName)
        => ParseHashField(SendRequest("get-branch", new Dictionary<string, object?> { ["name"] = branchName }), "hash");

    public override void SetBranch(string branchName, Hash hash, string? message = null)
        => SendRequest("set-branch", new Dictionary<string, object?>
        {
            ["name"] = branchName,
            ["hash"] = hash.ToHex(),
            ["message"] = message ?? ""
        });

    public override void CreateBranch(string branchName, Hash hash)
        => SendRequest("create-branch", new Dictionary<string, object?>
        {
            ["name"] = branchName,
            ["hash"] = hash.ToHex()
        });

    public override void DeleteBranch(string branchName)
        => SendRequest("delete-branch", new Dictionary<string, object?> { ["name"] = branchName });

    public override List<string> ListTags()
    {
        var result = new List<string>();
        var doc = SendRequest("list-tags");
        if (doc != null && doc.RootElement.TryGetProperty("tags", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in arr.EnumerateArray())
            {
                string? s = elem.GetString();
                if (s != null) result.Add(s);
            }
        }
        return result;
    }

    public override Hash? GetTag(string tagName)
        => ParseHashField(SendRequest("get-tag", new Dictionary<string, object?> { ["name"] = tagName }), "hash");

    public override void SetTag(string tagName, Hash hash, string? message = null)
        => SendRequest("set-tag", new Dictionary<string, object?>
        {
            ["name"] = tagName,
            ["hash"] = hash.ToHex(),
            ["message"] = message ?? ""
        });

    public override void DeleteTag(string tagName)
        => SendRequest("delete-tag", new Dictionary<string, object?> { ["name"] = tagName });

    public override Hash? GetRemoteBranch(string remoteName, string branchName)
        => ParseHashField(SendRequest("get-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName
        }), "hash");

    public override void SetRemoteBranch(string remoteName, string branchName, Hash hash)
        => SendRequest("set-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName,
            ["hash"] = hash.ToHex()
        });

    public override void DeleteRemoteBranch(string remoteName, string branchName)
        => SendRequest("delete-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName
        });

    public override List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null)
    {
        var result = new List<(string remote, string branch, Hash hash)>();
        var parameters = new Dictionary<string, object?>();
        if (remoteName != null) parameters["remote"] = remoteName;

        var doc = SendRequest("list-remote-branches", parameters);
        if (doc != null && doc.RootElement.TryGetProperty("remotes", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in arr.EnumerateArray())
            {
                string rn = elem.GetProperty("remote").GetString() ?? "";
                string bn = elem.GetProperty("branch").GetString() ?? "";
                string hx = elem.GetProperty("hash").GetString() ?? "";
                try { result.Add((rn, bn, Hash.Parse(hx))); } catch { }
            }
        }
        return result;
    }

    // ─── Objects ────────────────────────────────────────────────────

    public override byte[]? ReadObjectRaw(Hash hash)
    {
        var doc = SendRequest("read-object-raw", new Dictionary<string, object?> { ["hash"] = hash.ToHex() });
        string? b64 = GetStringField(doc, "data");
        if (b64 == null) return null;
        try { return Convert.FromBase64String(b64); } catch { return null; }
    }

    public override (ObjectType type, byte[] compressed)? ReadObjectWithType(Hash hash)
    {
        var doc = SendRequest("read-object-with-type", new Dictionary<string, object?> { ["hash"] = hash.ToHex() });
        string? typeStr = GetStringField(doc, "type");
        string? b64 = GetStringField(doc, "data");
        if (typeStr == null || b64 == null) return null;

        ObjectType type = ObjectType.Blob;
        try { type = Enum.Parse<ObjectType>(typeStr, true); } catch { }
        return (type, Convert.FromBase64String(b64));
    }

    public override bool Exists(Hash hash)
        => GetBoolField(SendRequest("object-exists", new Dictionary<string, object?> { ["hash"] = hash.ToHex() }), "exists");

    public override void WriteObjectRaw(Hash hash, byte[] compressedData, ObjectType type)
        => SendRequest("write-object-raw", new Dictionary<string, object?>
        {
            ["hash"] = hash.ToHex(),
            ["type"] = type.ToString(),
            ["data"] = Convert.ToBase64String(compressedData)
        });

    public override void WriteObjectsBatch(Dictionary<Hash, (ObjectType type, byte[] data)> objects)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var (hash, (type, data)) in objects)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["hash"] = hash.ToHex(),
                ["type"] = type.ToString(),
                ["data"] = Convert.ToBase64String(data)
            });
        }
        SendRequest("write-objects-batch", new Dictionary<string, object?> { ["objects"] = list });
    }

    // ─── Operations ─────────────────────────────────────────────────

    public override bool IsRepository()
        => GetBoolField(SendRequest("is-repository"), "result");

    public override Hash? FindMergeBase(Hash a, Hash b)
        => ParseHashField(SendRequest("find-merge-base", new Dictionary<string, object?>
        {
            ["a"] = a.ToHex(),
            ["b"] = b.ToHex()
        }), "base");

    public override bool IsAncestor(Hash ancestor, Hash descendant)
        => GetBoolField(SendRequest("is-ancestor", new Dictionary<string, object?>
        {
            ["ancestor"] = ancestor.ToHex(),
            ["descendant"] = descendant.ToHex()
        }), "result");

    public override Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjects(List<Hash> wants, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();

        var doc = SendRequest("collect-objects", new Dictionary<string, object?>
        {
            ["wants"] = wants.Select(w => w.ToHex()).ToList(),
            ["haves"] = haves.Select(h => h.ToHex()).ToList()
        });

        if (doc != null && doc.RootElement.TryGetProperty("objects", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in arr.EnumerateArray())
            {
                string hashHex = elem.GetProperty("hash").GetString() ?? "";
                string typeStr = elem.GetProperty("type").GetString() ?? "Blob";
                string dataB64 = elem.GetProperty("data").GetString() ?? "";

                Hash h = Hash.Parse(hashHex);
                ObjectType t = Enum.Parse<ObjectType>(typeStr, true);
                byte[] d = Convert.FromBase64String(dataB64);
                result[h] = (t, d);
            }
        }

        return result;
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            Disconnect();
        }
    }
}
