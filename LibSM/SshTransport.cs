using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SourceManager;

public class SshTransport : Transport
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _path;
    private readonly string? _remoteName;
    private readonly string? _user;
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private BinaryWriter? _bw;
    private BinaryReader? _br;
    private long _nextRequestId;
    private HashSet<string> _serverCapabilities = new();
    private readonly object _lock = new();

    private const string SmServeCommand = "sm serve --stdio";

    public SshTransport(string url, string? remoteName)
    {
        _remoteName = remoteName;

        string stripped = url.StartsWith("ssh://") ? url[6..] : url;

        int atIdx = stripped.IndexOf('@');
        string? user = null;
        string hostPart;
        if (atIdx >= 0)
        {
            user = stripped[..atIdx];
            hostPart = stripped[(atIdx + 1)..];
        }
        else
        {
            hostPart = stripped;
        }
        _user = string.IsNullOrEmpty(user) ? null : user;

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
        if (colonIdx >= 0)
        {
            _host = hostPart[..colonIdx];
            _port = int.Parse(hostPart[(colonIdx + 1)..]);
        }
        else
        {
            _host = hostPart;
            _port = 22;
        }
    }

    private void EnsureConnected()
    {
        lock (_lock)
        {
            if (_process != null && !_process.HasExited)
                return;

            Connect();
        }
    }

    private void Connect()
    {
        Disconnect();

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = BuildSshArgs(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ssh process");

        _reader = _process.StandardOutput;
        _writer = _process.StandardInput;
        _br = new BinaryReader(_process.StandardOutput.BaseStream, Encoding.UTF8);
        _bw = new BinaryWriter(_process.StandardInput.BaseStream, Encoding.UTF8);

        ReadHandshake();
    }

    private string BuildSshArgs()
    {
        var args = new StringBuilder();
        args.Append("-o StrictHostKeyChecking=accept-new ");
        args.Append("-o PasswordAuthentication=no ");
        args.Append("-o LogLevel=ERROR ");
        args.Append($"-p {_port} ");
        // ssh://user@host/... must connect AS that user; the user was parsed and then dropped,
        // so every ssh:// URL silently authenticated as the current local user.
        string target = _user != null ? $"{_user}@{_host}" : _host;
        args.Append($"{target} ");
        args.Append($"\"cd {EscapeShellArg(_path)} && {SmServeCommand}\"");
        return args.ToString();
    }

    private static string EscapeShellArg(string arg)
    {
        return arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
    }

    private void ReadHandshake()
    {
        try
        {
            string? line = _reader!.ReadLine();
            if (line == null) throw new InvalidOperationException("no hello from server");

            var hello = JsonDocument.Parse(line);
            string? status = hello.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "ready")
                throw new InvalidOperationException($"server did not become ready (status: {status ?? "missing"})");

            string? remoteVersion = hello.RootElement.TryGetProperty("version", out var v)
                ? v.GetString()
                : null;

            // Negotiate explicitly: version, capabilities and (optionally) a bearer token. A
            // protocol mismatch must fail here with a clear message, not later as a mysteriously
            // empty response.
            var parameters = new Dictionary<string, object?>
            {
                ["version"] = SmProtocol.Version,
                ["capabilities"] = SmProtocol.KnownCapabilities
            };
            string? token = ResolveToken();
            if (token != null) parameters["token"] = token;

            var negotiated = SendRequest("hello", parameters);
            if (negotiated == null)
                throw new InvalidOperationException("server did not answer the protocol handshake");

            var root = negotiated.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"server rejected the handshake: {err.GetString()}");

            string? agreedVersion = root.TryGetProperty("version", out var av) ? av.GetString() : null;
            if (!SmProtocol.IsCompatible(agreedVersion))
            {
                throw new InvalidOperationException(
                    $"protocol version mismatch: server speaks {agreedVersion ?? "unknown"}, " +
                    $"this client speaks {SmProtocol.Version}");
            }
            _serverCapabilities = root.TryGetProperty("capabilities", out var caps)
                ? caps.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToHashSet()
                : new HashSet<string>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SSH connection to {_host}:{_port} failed: {ex.Message}", ex);
        }
    }

    /// <summary>Bearer token for ssh:// URLs: SM_TOKEN or SM_TOKEN_&lt;REMOTE&gt;.</summary>
    private string? ResolveToken()
    {
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
        _bw?.Dispose();
        _br?.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        if (_process != null)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(); } catch { }
            }
            _process.Dispose();
        }
        _process = null;
        _reader = null;
        _writer = null;
        _br = null;
        _bw = null;
    }

    private JsonDocument? SendRequest(string method, Dictionary<string, object?>? parameters = null)
    {
        lock (_lock)
        {
            EnsureConnected();

            // Requests carry an id and the server echoes it, so a stray or unsolicited line can
            // never be mistaken for this request's response. Without correlation, one extra line
            // on the stream shifted every subsequent response by one.
            long id = ++_nextRequestId;
            var request = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };

            string json = TransportJson.Serialize(request);
            _writer!.WriteLine(json);
            _writer.Flush();

            // Read until the response carrying our id arrives.
            for (int guard = 0; guard < 1000; guard++)
            {
                string? respLine = _reader!.ReadLine();
                if (respLine == null)
                {
                    Disconnect();
                    return null;
                }

                if (respLine.Length == 0) continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(respLine);
                }
                catch
                {
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("id", out var idProp) ||
                    !idProp.TryGetInt64(out long gotId))
                {
                    // An id-less line is a notification (e.g. a warning); ignore it.
                    continue;
                }

                if (gotId == id) return doc;
            }

            Disconnect();
            return null;
        }
    }

    private string? GetStringField(JsonDocument? doc, string field)
    {
        return doc?.RootElement.TryGetProperty(field, out var val) == true ? val.GetString() : null;
    }

    private bool GetBoolField(JsonDocument? doc, string field)
    {
        return doc?.RootElement.TryGetProperty(field, out var val) == true && val.GetBoolean();
    }

    private Hash? ParseHashField(JsonDocument? doc, string field)
    {
        string? hex = GetStringField(doc, field);
        if (string.IsNullOrEmpty(hex)) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    // ─── Refs ───────────────────────────────────────────────────────

    public override Hash? GetHeadCommit()
    {
        var doc = SendRequest("get-head-commit");
        return ParseHashField(doc, "hash");
    }

    public override string? GetCurrentBranch()
    {
        var doc = SendRequest("get-current-branch");
        return GetStringField(doc, "branch");
    }

    public override List<BranchInfo> ListBranches()
    {
        var result = new List<BranchInfo>();
        var doc = SendRequest("list-branches");
        if (doc == null) return result;

        if (doc.RootElement.TryGetProperty("branches", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in arr.EnumerateArray())
            {
                result.Add(BranchInfo.FromJson(elem.GetRawText()));
            }
        }
        return result;
    }

    public override Hash? GetBranch(string branchName)
    {
        var doc = SendRequest("get-branch", new Dictionary<string, object?> { ["name"] = branchName });
        return ParseHashField(doc, "hash");
    }

    public override void SetBranch(string branchName, Hash hash, string? message = null)
    {
        SendRequest("set-branch", new Dictionary<string, object?>
        {
            ["name"] = branchName,
            ["hash"] = hash.ToHex(),
            ["message"] = message ?? ""
        });
    }

    public override void CreateBranch(string branchName, Hash hash)
    {
        SendRequest("create-branch", new Dictionary<string, object?>
        {
            ["name"] = branchName,
            ["hash"] = hash.ToHex()
        });
    }

    public override void DeleteBranch(string branchName)
    {
        SendRequest("delete-branch", new Dictionary<string, object?> { ["name"] = branchName });
    }

    public override List<string> ListTags()
    {
        var result = new List<string>();
        var doc = SendRequest("list-tags");
        if (doc == null) return result;

        if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
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
    {
        var doc = SendRequest("get-tag", new Dictionary<string, object?> { ["name"] = tagName });
        return ParseHashField(doc, "hash");
    }

    public override void SetTag(string tagName, Hash hash, string? message = null)
    {
        SendRequest("set-tag", new Dictionary<string, object?>
        {
            ["name"] = tagName,
            ["hash"] = hash.ToHex(),
            ["message"] = message ?? ""
        });
    }

    public override void DeleteTag(string tagName)
    {
        SendRequest("delete-tag", new Dictionary<string, object?> { ["name"] = tagName });
    }

    public override Hash? GetRemoteBranch(string remoteName, string branchName)
    {
        var doc = SendRequest("get-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName
        });
        return ParseHashField(doc, "hash");
    }

    public override void SetRemoteBranch(string remoteName, string branchName, Hash hash)
    {
        SendRequest("set-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName,
            ["hash"] = hash.ToHex()
        });
    }

    public override void DeleteRemoteBranch(string remoteName, string branchName)
    {
        SendRequest("delete-remote-branch", new Dictionary<string, object?>
        {
            ["remote"] = remoteName,
            ["branch"] = branchName
        });
    }

    public override List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null)
    {
        var result = new List<(string remote, string branch, Hash hash)>();
        var parameters = new Dictionary<string, object?>();
        if (remoteName != null) parameters["remote"] = remoteName;

        var doc = SendRequest("list-remote-branches", parameters);
        if (doc == null) return result;

        if (doc.RootElement.TryGetProperty("remotes", out var arr) && arr.ValueKind == JsonValueKind.Array)
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

        byte[] data = Convert.FromBase64String(b64);
        return (type, data);
    }

    public override bool Exists(Hash hash)
    {
        var doc = SendRequest("object-exists", new Dictionary<string, object?> { ["hash"] = hash.ToHex() });
        return GetBoolField(doc, "exists");
    }

    public override void WriteObjectRaw(Hash hash, byte[] compressedData, ObjectType type)
    {
        SendRequest("write-object-raw", new Dictionary<string, object?>
        {
            ["hash"] = hash.ToHex(),
            ["type"] = type.ToString(),
            ["data"] = Convert.ToBase64String(compressedData)
        });
    }

    public override async void WriteObjectsBatch(Dictionary<Hash, (ObjectType type, byte[] data)> objects)
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

        lock (_lock)
        {
            EnsureConnected();

            var request = new Dictionary<string, object?>
            {
                ["method"] = "write-objects-batch",
                ["params"] = new Dictionary<string, object?> { ["objects"] = list }
            };

            string json = TransportJson.Serialize(request);
            _writer!.WriteLine(json);
            _writer.Flush();

            string? respLine = _reader!.ReadLine();
        }
    }

    // ─── Operations ─────────────────────────────────────────────────

    public override bool IsRepository()
    {
        var doc = SendRequest("is-repository");
        return GetBoolField(doc, "result");
    }

    public override Hash? FindMergeBase(Hash a, Hash b)
    {
        var doc = SendRequest("find-merge-base", new Dictionary<string, object?>
        {
            ["a"] = a.ToHex(),
            ["b"] = b.ToHex()
        });
        return ParseHashField(doc, "base");
    }

    public override bool IsAncestor(Hash ancestor, Hash descendant)
    {
        var doc = SendRequest("is-ancestor", new Dictionary<string, object?>
        {
            ["ancestor"] = ancestor.ToHex(),
            ["descendant"] = descendant.ToHex()
        });
        return GetBoolField(doc, "result");
    }

    public override Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjects(List<Hash> wants, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();

        var doc = SendRequest("collect-objects", new Dictionary<string, object?>
        {
            ["wants"] = wants.Select(w => w.ToHex()).ToList(),
            ["haves"] = haves.Select(h => h.ToHex()).ToList()
        });

        if (doc == null) return result;

        if (doc.RootElement.TryGetProperty("objects", out var arr) && arr.ValueKind == JsonValueKind.Array)
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
            try
            {
                SendRequest("quit");
            }
            catch { }
            Disconnect();
        }
    }
}