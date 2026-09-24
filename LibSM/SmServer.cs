using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SourceManager;

public class SmServer
{
    private readonly Repository _repo;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public SmServer(string repoPath)
    {
        _repo = new Repository(repoPath);
    }

    // ─── HTTP Server Mode ───────────────────────────────────────────

    public async Task RunHttpAsync(string bindAddress, int port, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{bindAddress}:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            SmLog.WriteError("Permission denied. Run as Administrator or:");
            SmLog.WriteError($"  netsh http add urlacl url=http://+:{port}/ user=Everyone");
            return;
        }

        string prefix = _listener.Prefixes.First();
        SmLog.WriteLine($"SM server listening on {prefix}", SmLog.Color.BrightGreen);
        SmLog.WriteLine($"Serving: {_repo.RootPath}");

        if (RequiredToken() == null)
        {
            if (IsLoopback(bindAddress))
            {
                SmLog.WriteWarning("no authentication token set; relying on the loopback interface only.");
            }
            else
            {
                SmLog.WriteWarning("SECURITY: serving WITHOUT authentication on a non-loopback address.");
                SmLog.WriteWarning("  Anyone who can reach this port can push and rewrite refs.");
                SmLog.WriteWarning("  Set SM_SERVE_TOKEN=<secret> to require a bearer token.");
            }
        }
        else
        {
            SmLog.WriteLine("Bearer-token authentication is required.");
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WithCancellation(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = HandleRequestAsync(ctx);
            }
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            try { _listener.Stop(); } catch { }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }

    // ─── Native sm:// Protocol Server Mode ──────────────────────────

    /// <summary>
    /// Serves the native <c>sm://</c> binary protocol over TCP. Each connection performs a
    /// framed handshake (version + capabilities + optional bearer token), after which requests
    /// are dispatched through the same method table the stdio transport uses.
    /// </summary>
    public async Task RunTcpAsync(string bindAddress, int port, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IPAddress address;
        try
        {
            address = ResolveBindAddress(bindAddress);
        }
        catch (Exception ex)
        {
            SmLog.WriteError($"fatal: cannot resolve bind address '{bindAddress}': {ex.Message}");
            return;
        }

        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            SmLog.WriteError($"fatal: cannot listen on sm://{bindAddress}:{port}: {ex.Message}");
            return;
        }

        string display = bindAddress is "+" or "*" or "0.0.0.0" ? "0.0.0.0" : bindAddress;
        SmLog.WriteLine($"SM protocol server listening on sm://{display}:{port} ({SmWireProtocol.VersionTag}, frame v{SmWireProtocol.FrameVersion})", SmLog.Color.BrightGreen);
        SmLog.WriteLine($"Serving: {_repo.RootPath}");

        if (RequiredToken() == null)
        {
            if (IsLoopback(bindAddress))
            {
                SmLog.WriteWarning("no authentication token set; relying on the loopback interface only.");
            }
            else
            {
                SmLog.WriteWarning("SECURITY: serving WITHOUT authentication on a non-loopback address.");
                SmLog.WriteWarning("  Anyone who can reach this port can push and rewrite refs.");
                SmLog.WriteWarning("  Set SM_SERVE_TOKEN=<secret> to require a bearer token.");
            }
        }
        else
        {
            SmLog.WriteLine("Bearer-token authentication is required.");
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                _ = Task.Run(() => HandleTcpClient(client, _cts.Token));
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private void HandleTcpClient(TcpClient client, CancellationToken ct)
    {
        string peer = (client.Client.RemoteEndPoint as IPEndPoint)?.ToString() ?? "?";
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                SmLog.WriteLine($"  sm:// {peer} connected");

                SmFrameType type;
                string helloJson;
                try
                {
                    helloJson = SmWireProtocol.ReadFrameString(stream, out type);
                }
                catch (Exception ex)
                {
                    WriteTcpError(stream, $"bad handshake: {ex.Message}");
                    return;
                }

                if (type != SmFrameType.Hello)
                {
                    WriteTcpError(stream, "protocol error: expected a hello frame");
                    return;
                }

                JsonElement hello;
                try { hello = JsonDocument.Parse(helloJson).RootElement; }
                catch { WriteTcpError(stream, "protocol error: malformed hello"); return; }

                string? clientVersion = GetJsonString(hello, "version");
                if (!SmWireProtocol.IsCompatible(clientVersion))
                {
                    WriteTcpError(stream,
                        $"protocol version mismatch: client speaks {clientVersion ?? "unknown"}, " +
                        $"server speaks {SmWireProtocol.ProtocolVersion}");
                    return;
                }

                string? required = RequiredToken();
                if (!string.IsNullOrEmpty(required) &&
                    !TokenEquals(GetJsonString(hello, "token"), required))
                {
                    WriteTcpError(stream, "authentication failed: invalid token");
                    return;
                }

                SmWireProtocol.WriteFrame(stream, SmFrameType.HelloAck, TransportJson.Serialize(new
                {
                    protocol = SmWireProtocol.ProtocolName,
                    version = SmWireProtocol.ProtocolVersion,
                    frameVersion = SmWireProtocol.FrameVersion,
                    capabilities = SmProtocol.KnownCapabilities
                }));

                while (!ct.IsCancellationRequested)
                {
                    SmFrameType frameType;
                    byte[] body;
                    try
                    {
                        (frameType, body) = SmWireProtocol.ReadFrame(stream);
                    }
                    catch (EndOfStreamException) { break; }
                    catch (IOException) { break; }

                    if (frameType == SmFrameType.Bye) break;
                    if (frameType == SmFrameType.Ping)
                    {
                        SmWireProtocol.WriteFrame(stream, SmFrameType.Pong, "{}");
                        continue;
                    }
                    if (frameType != SmFrameType.Request) continue;

                    JsonDocument request;
                    try { request = JsonDocument.Parse(Encoding.UTF8.GetString(body)); }
                    catch
                    {
                        SmWireProtocol.WriteFrame(stream, SmFrameType.Error, ErrorJson("Invalid JSON"));
                        continue;
                    }

                    string? method = request.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
                    JsonElement? ps = request.RootElement.TryGetProperty("params", out var p) ? p : null;
                    long? requestId = request.RootElement.TryGetProperty("id", out var idElem) &&
                                      idElem.TryGetInt64(out long parsedId)
                        ? parsedId
                        : null;

                    string payload = HandleStdioMethod(method ?? "", ps);
                    SmWireProtocol.WriteFrame(stream, SmFrameType.Response, WithRequestId(payload, requestId));
                }
            }
        }
        catch (Exception ex)
        {
            SmLog.WriteError($"  sm:// {peer}: {ex.Message}");
        }
    }

    private static IPAddress ResolveBindAddress(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind) || bind is "+" or "*" || bind == "0.0.0.0")
            return IPAddress.Any;
        if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(bind, out IPAddress? parsed) && parsed != null)
            return parsed;
        IPAddress[] addresses = Dns.GetHostAddresses(bind);
        return addresses.FirstOrDefault() ?? IPAddress.Any;
    }

    private static void WriteTcpError(NetworkStream stream, string message)
    {
        try { SmWireProtocol.WriteFrame(stream, SmFrameType.Error, ErrorJson(message)); }
        catch { }
    }

    private static string? GetJsonString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static bool TokenEquals(string? presented, string required)
    {
        byte[] a = Encoding.UTF8.GetBytes(presented ?? "");
        byte[] b = Encoding.UTF8.GetBytes(required);
        return a.Length == b.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The bearer token this server requires, or null when running token-less.
    /// Set SM_SERVE_TOKEN (or remote.&lt;name&gt;.token in config) to require authentication.
    /// A token-less server is only appropriate on a loopback interface or behind a proxy that
    /// authenticates — the startup banner says so explicitly.
    /// </summary>
    private static string? RequiredToken()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SM_SERVE_TOKEN");
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }

    private static bool IsAuthorized(HttpListenerContext ctx)
    {
        string? required = RequiredToken();
        if (required == null) return true;

        string? header = ctx.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)) return false;
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

        string presented = header[7..].Trim();
        byte[] a = System.Text.Encoding.UTF8.GetBytes(presented);
        byte[] b = System.Text.Encoding.UTF8.GetBytes(required);

        // FixedTimeEquals requires equal lengths; compare lengths first, then the bytes in
        // constant time.
        return a.Length == b.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>True when the bind address is only reachable from this machine.</summary>
    private static bool IsLoopback(string bind)
        => bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           bind.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           bind.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            string? path = ctx.Request.Url?.AbsolutePath ?? "/";
            string method = ctx.Request.HttpMethod;

            SmLog.WriteLine($"  {method} {path}");

            // Authenticate before dispatching. Without this any client that could reach the port
            // could push objects, rewrite refs and delete tags.
            if (!IsAuthorized(ctx))
            {
                WriteError(ctx, 401, "Unauthorized");
                return;
            }

            switch (method)
            {
                case "GET":
                    await HandleGetAsync(ctx, path);
                    break;
                case "POST":
                    await HandlePostAsync(ctx, path);
                    break;
                case "PUT":
                    await HandlePutAsync(ctx, path);
                    break;
                case "DELETE":
                    await HandleDeleteAsync(ctx, path);
                    break;
                default:
                    WriteError(ctx, 405, "Method Not Allowed");
                    break;
            }
        }
        catch (Exception ex)
        {
            try { WriteError(ctx, 500, $"Internal Error: {ex.Message}"); }
            catch { }
        }
    }

    private async Task HandleGetAsync(HttpListenerContext ctx, string path)
    {
        if (path == TransportProtocol.IsRepoEndpoint || path == TransportProtocol.HealthEndpoint)
        {
            WriteJson(ctx, new { status = "ok", version = TransportProtocol.ProtocolVersion });
            return;
        }

        if (path == TransportProtocol.HeadEndpoint)
        {
            HandleGetHead(ctx);
            return;
        }

        if (path == TransportProtocol.BranchesEndpoint)
        {
            HandleGetBranches(ctx);
            return;
        }

        if (path.StartsWith(TransportProtocol.BranchesEndpoint + "/"))
        {
            string branchName = Uri.UnescapeDataString(path[(TransportProtocol.BranchesEndpoint.Length + 1)..]);
            HandleGetBranch(ctx, branchName);
            return;
        }

        if (path == TransportProtocol.TagsEndpoint)
        {
            HandleGetTags(ctx);
            return;
        }

        if (path.StartsWith(TransportProtocol.TagsEndpoint + "/"))
        {
            string tagName = Uri.UnescapeDataString(path[(TransportProtocol.TagsEndpoint.Length + 1)..]);
            HandleGetTag(ctx, tagName);
            return;
        }

        if (path.StartsWith(TransportProtocol.RemotesEndpoint + "/"))
        {
            string rest = path[(TransportProtocol.RemotesEndpoint.Length + 1)..];
            HandleGetRemote(ctx, rest);
            return;
        }

        if (path == TransportProtocol.RemotesEndpoint)
        {
            HandleGetRemotes(ctx);
            return;
        }

        // More specific object routes must be tested before the generic /objects/<hash> route,
        // otherwise "/sm/objects/check/<hash>" is consumed as an object id named "check".
        if (path.StartsWith(TransportProtocol.ObjectsCheckEndpoint + "/"))
        {
            string hashHex = path[(TransportProtocol.ObjectsCheckEndpoint.Length + 1)..];
            HandleCheckObject(ctx, hashHex);
            return;
        }

        if (path.StartsWith(TransportProtocol.ObjectsEndpoint + "/"))
        {
            string rest = path[(TransportProtocol.ObjectsEndpoint.Length + 1)..];
            HandleGetObject(ctx, rest);
            return;
        }

        WriteError(ctx, 404, "Not Found");
    }

    private async Task HandlePostAsync(HttpListenerContext ctx, string path)
    {
        if (path.StartsWith(TransportProtocol.BranchesEndpoint + "/create"))
        {
            var body = await ReadJsonBodyAsync(ctx);
            string name = GetStringProp(body, "name") ?? "";
            string hashHex = GetStringProp(body, "hash") ?? "";
            HandleCreateBranch(ctx, name, hashHex);
            return;
        }

        if (path.StartsWith(TransportProtocol.BranchesEndpoint + "/"))
        {
            string branchName = Uri.UnescapeDataString(path[(TransportProtocol.BranchesEndpoint.Length + 1)..]);
            var body = await ReadJsonBodyAsync(ctx);
            string hashHex = GetStringProp(body, "hash") ?? "";
            string message = GetStringProp(body, "message") ?? "";
            HandleSetBranch(ctx, branchName, hashHex, message);
            return;
        }

        if (path.StartsWith(TransportProtocol.TagsEndpoint + "/"))
        {
            string tagName = Uri.UnescapeDataString(path[(TransportProtocol.TagsEndpoint.Length + 1)..]);
            var body = await ReadJsonBodyAsync(ctx);
            string hashHex = GetStringProp(body, "hash") ?? "";
            string message = GetStringProp(body, "message") ?? "";
            HandleSetTag(ctx, tagName, hashHex, message);
            return;
        }

        if (path.StartsWith(TransportProtocol.RemotesEndpoint + "/"))
        {
            string rest = path[(TransportProtocol.RemotesEndpoint.Length + 1)..];
            var body = await ReadJsonBodyAsync(ctx);
            HandleSetRemote(ctx, rest, body);
            return;
        }

        // Exact-match routes first: "/sm/objects/batch" also satisfies the generic
        // "/sm/objects/<hash>" prefix test, and being shadowed by it made every batch object
        // upload fail with "Invalid hash length: 5" — push then reported success having
        // transferred nothing.
        if (path == TransportProtocol.ObjectsBatchEndpoint)
        {
            var body = await ReadJsonBodyAsync(ctx);
            HandleWriteObjectsBatch(ctx, body);
            return;
        }

        if (path.StartsWith(TransportProtocol.ObjectsEndpoint + "/"))
        {
            string hashHex = path[(TransportProtocol.ObjectsEndpoint.Length + 1)..].TrimStart('/');
            var body = await ReadJsonBodyAsync(ctx);
            HandleWriteObject(ctx, hashHex, body);
            return;
        }

        if (path == TransportProtocol.MergeBaseEndpoint)
        {
            var body = await ReadJsonBodyAsync(ctx);
            string aHex = GetStringProp(body, "a") ?? "";
            string bHex = GetStringProp(body, "b") ?? "";
            HandleMergeBase(ctx, aHex, bHex);
            return;
        }

        if (path == TransportProtocol.AncestorEndpoint)
        {
            var body = await ReadJsonBodyAsync(ctx);
            string ancHex = GetStringProp(body, "ancestor") ?? "";
            string desHex = GetStringProp(body, "descendant") ?? "";
            HandleAncestor(ctx, ancHex, desHex);
            return;
        }

        if (path == TransportProtocol.CollectObjectsEndpoint)
        {
            var body = await ReadJsonBodyAsync(ctx);
            HandleCollectObjects(ctx, body);
            return;
        }

        WriteError(ctx, 404, "Not Found");
    }

    private async Task HandlePutAsync(HttpListenerContext ctx, string path)
    {
        WriteError(ctx, 405, "Method Not Allowed");
    }

    private async Task HandleDeleteAsync(HttpListenerContext ctx, string path)
    {
        if (path.StartsWith(TransportProtocol.BranchesEndpoint + "/"))
        {
            string branchName = Uri.UnescapeDataString(path[(TransportProtocol.BranchesEndpoint.Length + 1)..]);
            HandleDeleteBranch(ctx, branchName);
            return;
        }

        if (path.StartsWith(TransportProtocol.TagsEndpoint + "/"))
        {
            string tagName = Uri.UnescapeDataString(path[(TransportProtocol.TagsEndpoint.Length + 1)..]);
            HandleDeleteTag(ctx, tagName);
            return;
        }

        if (path.StartsWith(TransportProtocol.RemotesEndpoint + "/"))
        {
            string rest = path[(TransportProtocol.RemotesEndpoint.Length + 1)..];
            HandleDeleteRemote(ctx, rest);
            return;
        }

        WriteError(ctx, 404, "Not Found");
    }

    // ─── Handlers: Head ─────────────────────────────────────────────

    private void HandleGetHead(HttpListenerContext ctx)
    {
        Hash? head = _repo.Refs.GetHeadCommit();
        string? branch = _repo.Refs.GetCurrentBranch();
        WriteJson(ctx, new
        {
            hash = head?.ToHex(),
            branch = (string?)branch
        });
    }

    // ─── Handlers: Branches ─────────────────────────────────────────

    private void HandleGetBranches(HttpListenerContext ctx)
    {
        var branches = _repo.Refs.ListBranches();
        WriteJson(ctx, branches.Select(b => new
        {
            name = b.Name,
            hash = b.TipHash.ToHex(),
            upstream = b.UpstreamBranch,
            remote = b.RemoteName,
            isHead = b.IsHead
        }));
    }

    private void HandleGetBranch(HttpListenerContext ctx, string branchName)
    {
        Hash? hash = _repo.Refs.GetBranch(branchName);
        if (hash == null)
        {
            WriteError(ctx, 404, $"Branch '{branchName}' not found");
            return;
        }
        WriteJson(ctx, new { name = branchName, hash = hash.Value.ToHex() });
    }

    private void HandleSetBranch(HttpListenerContext ctx, string branchName, string hashHex, string? message)
    {
        try
        {
            Hash hash = Hash.Parse(hashHex);
            _repo.Refs.SetBranch(branchName, hash, message);
            WriteJson(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Bad request: {ex.Message}");
        }
    }

    private void HandleCreateBranch(HttpListenerContext ctx, string name, string hashHex)
    {
        try
        {
            Hash hash = Hash.Parse(hashHex);
            _repo.Refs.CreateBranch(name, hash);
            WriteJson(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Bad request: {ex.Message}");
        }
    }

    private void HandleDeleteBranch(HttpListenerContext ctx, string branchName)
    {
        try
        {
            _repo.Refs.DeleteBranch(branchName);
            WriteJson(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 404, ex.Message);
        }
    }

    // ─── Handlers: Tags ─────────────────────────────────────────────

    private void HandleGetTags(HttpListenerContext ctx)
    {
        var tags = _repo.Refs.ListTags();
        WriteJson(ctx, tags);
    }

    private void HandleGetTag(HttpListenerContext ctx, string tagName)
    {
        Hash? hash = _repo.Refs.GetTag(tagName);
        if (hash == null)
        {
            WriteError(ctx, 404, $"Tag '{tagName}' not found");
            return;
        }
        WriteJson(ctx, new { name = tagName, hash = hash.Value.ToHex() });
    }

    private void HandleSetTag(HttpListenerContext ctx, string tagName, string hashHex, string? message)
    {
        try
        {
            Hash hash = Hash.Parse(hashHex);
            _repo.Refs.SetTag(tagName, hash, message);
            WriteJson(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Bad request: {ex.Message}");
        }
    }

    private void HandleDeleteTag(HttpListenerContext ctx, string tagName)
    {
        try
        {
            _repo.Refs.DeleteTag(tagName);
            WriteJson(ctx, new { ok = true });
        }
        catch
        {
            WriteError(ctx, 404, $"Tag '{tagName}' not found");
        }
    }

    // ─── Handlers: Remotes ──────────────────────────────────────────

    private void HandleGetRemotes(HttpListenerContext ctx)
    {
        var branches = _repo.Refs.ListRemoteBranches();
        var list = branches.Select(rb => new
        {
            remote = rb.remote,
            branch = rb.branch,
            hash = rb.hash.ToHex()
        });
        WriteJson(ctx, list);
    }

    private void HandleGetRemote(HttpListenerContext ctx, string rest)
    {
        string[] parts = rest.Split('/');
        if (parts.Length == 1)
        {
            string remoteName = Uri.UnescapeDataString(parts[0]);
            var branches = _repo.Refs.ListRemoteBranches(remoteName);
            WriteJson(ctx, branches.Select(rb => new
            {
                remote = rb.remote,
                branch = rb.branch,
                hash = rb.hash.ToHex()
            }));
        }
        else if (parts.Length >= 2)
        {
            string remoteName = Uri.UnescapeDataString(parts[0]);
            string branchName = Uri.UnescapeDataString(parts[1]);
            Hash? hash = _repo.Refs.GetRemoteBranch(remoteName, branchName);
            if (hash == null)
            {
                WriteError(ctx, 404, $"Remote branch '{remoteName}/{branchName}' not found");
                return;
            }
            WriteJson(ctx, new
            {
                remote = remoteName,
                branch = branchName,
                hash = hash.Value.ToHex()
            });
        }
    }

    private void HandleSetRemote(HttpListenerContext ctx, string rest, JsonDocument body)
    {
        string[] parts = rest.Split('/');
        if (parts.Length < 2)
        {
            WriteError(ctx, 400, "URL must be /sm/refs/remotes/{remote}/{branch}");
            return;
        }

        string remoteName = Uri.UnescapeDataString(parts[0]);
        string branchName = Uri.UnescapeDataString(parts[1]);
        string hashHex = GetStringProp(body, "hash") ?? "";
        string message = GetStringProp(body, "message") ?? "";

        try
        {
            Hash hash = Hash.Parse(hashHex);
            _repo.Refs.SetRemoteBranch(remoteName, branchName, hash);
            WriteJson(ctx, new { ok = true });
        }
        catch
        {
            WriteError(ctx, 400, "Invalid hash");
        }
    }

    private void HandleDeleteRemote(HttpListenerContext ctx, string rest)
    {
        string[] parts = rest.Split('/');
        if (parts.Length < 2)
        {
            WriteError(ctx, 400, "URL must be /sm/refs/remotes/{remote}/{branch}");
            return;
        }

        string remoteName = Uri.UnescapeDataString(parts[0]);
        string branchName = Uri.UnescapeDataString(parts[1]);

        _repo.Refs.DeleteRemoteBranch(remoteName, branchName);
        WriteJson(ctx, new { ok = true });
    }

    // ─── Handlers: Objects ──────────────────────────────────────────

    private void HandleGetObject(HttpListenerContext ctx, string rest)
    {
        if (rest.EndsWith("/meta"))
        {
            string hashHex = rest[..^5];
            Hash? hash = Hash.TryParse(hashHex);
            if (hash == null)
            {
                WriteError(ctx, 400, "Invalid hash");
                return;
            }

            var obj = _repo.Objects.ReadObjectWithType(hash.Value);
            if (!obj.HasValue)
            {
                WriteError(ctx, 404, "Object not found");
                return;
            }
            WriteJson(ctx, new { type = obj.Value.type.ToString() });
            return;
        }

        {
            Hash? hash = Hash.TryParse(rest);
            if (hash == null)
            {
                WriteError(ctx, 400, "Invalid hash");
                return;
            }

            byte[]? raw = _repo.Objects.ReadObjectRaw(hash.Value);
            if (raw == null)
            {
                WriteError(ctx, 404, "Object not found");
                return;
            }

            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Write(raw, 0, raw.Length);
            ctx.Response.Close();
        }
    }

    private void HandleCheckObject(HttpListenerContext ctx, string hashHex)
    {
        Hash? hash = Hash.TryParse(hashHex);
        if (hash == null)
        {
            WriteError(ctx, 400, "Invalid hash");
            return;
        }

        bool exists = _repo.Objects.Exists(hash.Value);
        ctx.Response.StatusCode = exists ? 200 : 404;
        ctx.Response.Close();
    }

    private void HandleWriteObject(HttpListenerContext ctx, string hashHex, JsonDocument body)
    {
        try
        {
            Hash hash = Hash.Parse(hashHex);
            string typeStr = GetStringProp(body, "type") ?? "Blob";
            string dataB64 = GetStringProp(body, "data") ?? "";

            ObjectType type = Enum.Parse<ObjectType>(typeStr, true);
            byte[] data = Convert.FromBase64String(dataB64);

            // Wire payloads are raw; WritePayload frames and compresses them for storage.
            _repo.Objects.WritePayload(hash, type, data);
            WriteJson(ctx, new { ok = true });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Write failed: {ex.Message}");
        }
    }

    private void HandleWriteObjectsBatch(HttpListenerContext ctx, JsonDocument body)
    {
        try
        {
            if (!body.RootElement.TryGetProperty("objects", out var arr))
            {
                WriteError(ctx, 400, "Missing 'objects' array");
                return;
            }

            int count = 0;
            foreach (JsonElement elem in arr.EnumerateArray())
            {
                string hashHex = elem.GetProperty("hash").GetString() ?? "";
                string typeStr = elem.GetProperty("type").GetString() ?? "Blob";
                string dataB64 = elem.GetProperty("data").GetString() ?? "";

                Hash hash = Hash.Parse(hashHex);
                ObjectType type = Enum.Parse<ObjectType>(typeStr, true);
                byte[] data = Convert.FromBase64String(dataB64);

                _repo.Objects.WritePayload(hash, type, data);
                count++;
            }

            WriteJson(ctx, new { ok = true, count });
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Batch write failed: {ex.Message}");
        }
    }

    // ─── Handlers: Merge Base / Ancestor ────────────────────────────

    private void HandleMergeBase(HttpListenerContext ctx, string aHex, string bHex)
    {
        try
        {
            Hash a = Hash.Parse(aHex);
            Hash b = Hash.Parse(bHex);
            Hash? mb = _repo.FindMergeBase(a, b);
            WriteJson(ctx, new { @base = mb?.ToHex() ?? "" });
        }
        catch
        {
            WriteError(ctx, 400, "Invalid hash");
        }
    }

    private void HandleAncestor(HttpListenerContext ctx, string ancHex, string desHex)
    {
        try
        {
            Hash ancestor = Hash.Parse(ancHex);
            Hash descendant = Hash.Parse(desHex);
            bool result = IsAncestorOf(_repo, ancestor, descendant);
            WriteJson(ctx, new { result });
        }
        catch
        {
            WriteError(ctx, 400, "Invalid hash");
        }
    }

    private static bool IsAncestorOf(Repository repo, Hash ancestor, Hash descendant)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<Hash>();
        queue.Enqueue(descendant);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            if (!visited.Add(current.ToHex())) continue;
            if (current.Equals(ancestor)) return true;

            Commit? commit = repo.Objects.ReadCommit(current);
            if (commit != null)
            {
                foreach (Hash p in commit.ParentHashes)
                {
                    if (!visited.Contains(p.ToHex()))
                        queue.Enqueue(p);
                }
            }
        }
        return false;
    }

    // ─── Handlers: Collect Objects ──────────────────────────────────

    private void HandleCollectObjects(HttpListenerContext ctx, JsonDocument body)
    {
        try
        {
            var wantHexes = new List<string>();
            if (body.RootElement.TryGetProperty("wants", out var wa))
            {
                foreach (JsonElement e in wa.EnumerateArray())
                {
                    string? s = e.GetString();
                    if (s != null) wantHexes.Add(s);
                }
            }

            var haveHexes = new List<string>();
            if (body.RootElement.TryGetProperty("haves", out var ha))
            {
                foreach (JsonElement e in ha.EnumerateArray())
                {
                    string? s = e.GetString();
                    if (s != null) haveHexes.Add(s);
                }
            }

            var wants = wantHexes.Select(Hash.Parse).ToList();
            var haves = haveHexes.Select(h => Hash.TryParse(h)).Where(h => h.HasValue).Select(h => h!.Value).ToList();
            var haveSet = new HashSet<string>(haveHexes);

            var result = new List<object>();
            var visited = new HashSet<string>();
            var queue = new Queue<Hash>(wants);

            while (queue.Count > 0)
            {
                Hash current = queue.Dequeue();
                string hex = current.ToHex();
                if (!visited.Add(hex)) continue;
                if (haveSet.Contains(hex)) continue;

                var obj = _repo.Objects.ReadObjectWithType(current);
                if (!obj.HasValue) continue;

                result.Add(new
                {
                    hash = hex,
                    type = obj.Value.type.ToString(),
                    data = Convert.ToBase64String(obj.Value.data)
                });

                if (obj.Value.type == ObjectType.Commit)
                {
                    Commit? commit = ObjectStore.DeserializeCommit(obj.Value.data);
                    if (commit != null)
                    {
                        foreach (Hash p in commit.ParentHashes)
                            queue.Enqueue(p);
                        if (commit.TreeHash.HasValue)
                            CollectTreeObjectsServer(commit.TreeHash.Value, result, visited, haveSet);
                    }
                }
                else if (obj.Value.type == ObjectType.Tag)
                {
                    TagObject? tag = ObjectStore.DeserializeTag(obj.Value.data);
                    if (tag != null)
                        queue.Enqueue(tag.TargetHash);
                }
            }

            WriteJson(ctx, result);
        }
        catch (Exception ex)
        {
            WriteError(ctx, 400, $"Collect objects failed: {ex.Message}");
        }
    }

    private void CollectTreeObjectsServer(Hash treeHash, List<object> result, HashSet<string> visited, HashSet<string> haveSet)
    {
        var queue = new Queue<Hash>();
        queue.Enqueue(treeHash);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = _repo.Objects.ReadObjectWithType(current);
            if (!obj.HasValue) continue;

            result.Add(new
            {
                hash = hex,
                type = obj.Value.type.ToString(),
                data = Convert.ToBase64String(obj.Value.data)
            });

            if (obj.Value.type == ObjectType.Tree)
            {
                Tree? tree = ObjectStore.DeserializeTree(obj.Value.data);
                if (tree != null)
                {
                    foreach (var entry in tree.Entries)
                    {
                        string entryHex = entry.ObjectHash.ToHex();
                        if (!visited.Contains(entryHex) && !haveSet.Contains(entryHex))
                        {
                            var blobObj = _repo.Objects.ReadObjectWithType(entry.ObjectHash);
                            if (blobObj.HasValue)
                            {
                                result.Add(new
                                {
                                    hash = entryHex,
                                    type = blobObj.Value.type.ToString(),
                                    data = Convert.ToBase64String(blobObj.Value.data)
                                });
                                visited.Add(entryHex);
                            }
                        }

                        if (entry.Mode == FileMode.Directory)
                        {
                            CollectTreeObjectsServer(entry.ObjectHash, result, visited, haveSet);
                        }
                    }
                }
            }
        }
    }

    // ─── JSON & HTTP Helpers ────────────────────────────────────────

    private static void WriteJson(HttpListenerContext ctx, object data)
    {
        string json = TransportJson.Serialize(data);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.StatusCode = 200;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static void WriteError(HttpListenerContext ctx, int code, string message)
    {
        string json = TransportJson.Serialize(new { error = message, status = code });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.StatusCode = code;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static async Task<JsonDocument?> ReadJsonBodyAsync(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            string json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}");
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProp(JsonDocument? doc, string prop)
    {
        if (doc == null) return null;
        return doc.RootElement.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }

    // ─── Stdio Mode (for SSH transport) ─────────────────────────────

    public async Task RunStdioAsync(CancellationToken ct = default)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var reader = new StreamReader(stdin, Encoding.UTF8);
        var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };

        writer.WriteLine(TransportJson.Serialize(new
        {
            status = "ready",
            protocol = "sm",
            version = SmProtocol.Version,
            capabilities = SmProtocol.KnownCapabilities
        }));
        writer.Flush();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch
            {
                break;
            }

            if (line == null) break;

            // A blank line is a liveness probe or padding, never a request. Replying to it put an
            // extra message on the stream and desynchronised every following request/response
            // pair, so the client saw the previous response for every call.
            if (line.Length == 0 || line.Trim().Length == 0) continue;

            JsonDocument? request;
            try
            {
                request = JsonDocument.Parse(line);
            }
            catch
            {
                writer.WriteLine(TransportJson.Serialize(new { id = (long?)null, error = "Invalid JSON" }));
                writer.Flush();
                continue;
            }

            string? method = request.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
            JsonElement? paramsElem = request.RootElement.TryGetProperty("params", out var p) ? p : null;
            long? requestId = request.RootElement.TryGetProperty("id", out var idElem) &&
                              idElem.TryGetInt64(out long parsedId)
                ? parsedId
                : null;

            string payload = HandleStdioMethod(method ?? "", paramsElem);
            writer.WriteLine(WithRequestId(payload, requestId));
            writer.Flush();
        }
    }

    /// <summary>
    /// Re-emits a handler response with the caller's request id attached, so the client can
    /// match responses to requests instead of assuming strict ordering.
    /// </summary>
    private static string WithRequestId(string payload, long? requestId)
    {
        if (requestId == null) return payload;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return payload;

            var fields = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                fields[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            fields["id"] = requestId.Value;
            return TransportJson.Serialize(fields);
        }
        catch
        {
            return payload;
        }
    }

    private string HandleStdioMethod(string method, JsonElement? ps)
    {
        try
        {
            switch (method)
            {
                case "hello":
                {
                    // Explicit version negotiation and optional token authentication. The server
                    // answers with what it agreed to, and refuses an incompatible major version
                    // instead of letting the client fail later in a confusing way.
                    string? clientVersion = GetParamStr(ps, "version");
                    if (!SmProtocol.IsCompatible(clientVersion))
                    {
                        return TransportJson.Serialize(new
                        {
                            error = $"protocol version mismatch: client speaks {clientVersion ?? "unknown"}, " +
                                    $"server speaks {SmProtocol.Version}"
                        });
                    }

                    string? required = Environment.GetEnvironmentVariable("SM_SERVE_TOKEN");
                    if (!string.IsNullOrEmpty(required))
                    {
                        string? presented = GetParamStr(ps, "token");
                        byte[] a = System.Text.Encoding.UTF8.GetBytes(presented ?? "");
                        byte[] b = System.Text.Encoding.UTF8.GetBytes(required);
                        bool ok = a.Length == b.Length &&
                                  System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
                        if (!ok)
                            return TransportJson.Serialize(new { error = "authentication failed: invalid token" });
                    }

                    return TransportJson.Serialize(new
                    {
                        version = SmProtocol.Version,
                        capabilities = SmProtocol.KnownCapabilities
                    });
                }
                case "get-head-commit":
                {
                    Hash? h = _repo.Refs.GetHeadCommit();
                    return TransportJson.Serialize(new { hash = h?.ToHex(), branch = _repo.Refs.GetCurrentBranch() });
                }
                case "get-current-branch":
                {
                    return TransportJson.Serialize(new { branch = _repo.Refs.GetCurrentBranch() });
                }
                case "list-branches":
                {
                    var branches = _repo.Refs.ListBranches();
                    var list = branches.Select(b => new
                    {
                        name = b.Name,
                        hash = b.TipHash.ToHex(),
                        upstream = b.UpstreamBranch,
                        remote = b.RemoteName,
                        isHead = b.IsHead
                    });
                    return TransportJson.Serialize(new { branches = list });
                }
                case "get-branch":
                {
                    string? name = GetParamStr(ps, "name");
                    if (name == null) return ErrorJson("Missing 'name'");
                    Hash? h = _repo.Refs.GetBranch(name);
                    return TransportJson.Serialize(new { hash = h?.ToHex() ?? "" });
                }
                case "set-branch":
                {
                    string? name = GetParamStr(ps, "name");
                    string? hashHex = GetParamStr(ps, "hash");
                    string? message = GetParamStr(ps, "message");
                    if (name == null || hashHex == null) return ErrorJson("Missing name or hash");
                    _repo.Refs.SetBranch(name, Hash.Parse(hashHex), message);
                    return TransportJson.Serialize(new { ok = true });
                }
                case "create-branch":
                {
                    string? name = GetParamStr(ps, "name");
                    string? hashHex = GetParamStr(ps, "hash");
                    if (name == null || hashHex == null) return ErrorJson("Missing name or hash");
                    _repo.Refs.CreateBranch(name, Hash.Parse(hashHex));
                    return TransportJson.Serialize(new { ok = true });
                }
                case "delete-branch":
                {
                    string? name = GetParamStr(ps, "name");
                    if (name == null) return ErrorJson("Missing 'name'");
                    _repo.Refs.DeleteBranch(name);
                    return TransportJson.Serialize(new { ok = true });
                }
                case "list-tags":
                {
                    return TransportJson.Serialize(new { tags = _repo.Refs.ListTags() });
                }
                case "get-tag":
                {
                    string? name = GetParamStr(ps, "name");
                    if (name == null) return ErrorJson("Missing 'name'");
                    Hash? h = _repo.Refs.GetTag(name);
                    return TransportJson.Serialize(new { hash = h?.ToHex() ?? "" });
                }
                case "set-tag":
                {
                    string? name = GetParamStr(ps, "name");
                    string? hashHex = GetParamStr(ps, "hash");
                    string? message = GetParamStr(ps, "message");
                    if (name == null || hashHex == null) return ErrorJson("Missing name or hash");
                    _repo.Refs.SetTag(name, Hash.Parse(hashHex), message);
                    return TransportJson.Serialize(new { ok = true });
                }
                case "delete-tag":
                {
                    string? name = GetParamStr(ps, "name");
                    if (name == null) return ErrorJson("Missing 'name'");
                    _repo.Refs.DeleteTag(name);
                    return TransportJson.Serialize(new { ok = true });
                }
                case "get-remote-branch":
                {
                    string? remote = GetParamStr(ps, "remote");
                    string? branch = GetParamStr(ps, "branch");
                    if (remote == null || branch == null) return ErrorJson("Missing remote or branch");
                    Hash? h = _repo.Refs.GetRemoteBranch(remote, branch);
                    return TransportJson.Serialize(new { hash = h?.ToHex() ?? "" });
                }
                case "set-remote-branch":
                {
                    string? remote = GetParamStr(ps, "remote");
                    string? branch = GetParamStr(ps, "branch");
                    string? hashHex = GetParamStr(ps, "hash");
                    if (remote == null || branch == null || hashHex == null) return ErrorJson("Missing remote, branch, or hash");
                    _repo.Refs.SetRemoteBranch(remote, branch, Hash.Parse(hashHex));
                    return TransportJson.Serialize(new { ok = true });
                }
                case "delete-remote-branch":
                {
                    string? remote = GetParamStr(ps, "remote");
                    string? branch = GetParamStr(ps, "branch");
                    if (remote == null || branch == null) return ErrorJson("Missing remote or branch");
                    _repo.Refs.DeleteRemoteBranch(remote, branch);
                    return TransportJson.Serialize(new { ok = true });
                }
                case "list-remote-branches":
                {
                    string? remote = GetParamStr(ps, "remote");
                    var branches = _repo.Refs.ListRemoteBranches(remote);
                    var list = branches.Select(rb => new { remote = rb.remote, branch = rb.branch, hash = rb.hash.ToHex() });
                    return TransportJson.Serialize(new { remotes = list });
                }
                case "read-object-raw":
                {
                    string? hashHex = GetParamStr(ps, "hash");
                    if (hashHex == null) return ErrorJson("Missing 'hash'");
                    byte[]? raw = _repo.Objects.ReadObjectRaw(Hash.Parse(hashHex));
                    return TransportJson.Serialize(new { data = raw != null ? Convert.ToBase64String(raw) : null });
                }
                case "read-object-with-type":
                {
                    string? hashHex = GetParamStr(ps, "hash");
                    if (hashHex == null) return ErrorJson("Missing 'hash'");
                    var obj = _repo.Objects.ReadObjectWithType(Hash.Parse(hashHex));
                    if (!obj.HasValue) return TransportJson.Serialize(new { data = (string?)null, type = "" });
                    return TransportJson.Serialize(new { data = Convert.ToBase64String(obj.Value.data), type = obj.Value.type.ToString() });
                }
                case "object-exists":
                {
                    string? hashHex = GetParamStr(ps, "hash");
                    if (hashHex == null) return ErrorJson("Missing 'hash'");
                    return TransportJson.Serialize(new { exists = _repo.Objects.Exists(Hash.Parse(hashHex)) });
                }
                case "write-object-raw":
                {
                    string? hashHex = GetParamStr(ps, "hash");
                    string? typeStr = GetParamStr(ps, "type");
                    string? dataB64 = GetParamStr(ps, "data");
                    if (hashHex == null || dataB64 == null) return ErrorJson("Missing hash or data");
                    ObjectType type = ObjectType.Blob;
                    if (typeStr != null) Enum.TryParse(typeStr, true, out type);
                    _repo.Objects.WritePayload(Hash.Parse(hashHex), type, Convert.FromBase64String(dataB64));
                    return TransportJson.Serialize(new { ok = true });
                }
                case "write-objects-batch":
                {
                    if (!ps.HasValue || !ps.Value.TryGetProperty("objects", out var arr)) return ErrorJson("Missing 'objects' array");
                    int count = 0;
                    foreach (JsonElement elem in arr.EnumerateArray())
                    {
                        string hx = elem.GetProperty("hash").GetString() ?? "";
                        string ts = elem.GetProperty("type").GetString() ?? "Blob";
                        string db = elem.GetProperty("data").GetString() ?? "";
                        ObjectType t = Enum.Parse<ObjectType>(ts, true);
                        _repo.Objects.WritePayload(Hash.Parse(hx), t, Convert.FromBase64String(db));
                        count++;
                    }
                    return TransportJson.Serialize(new { ok = true, count });
                }
                case "is-repository":
                {
                    return TransportJson.Serialize(new { result = true });
                }
                case "find-merge-base":
                {
                    string? aHex = GetParamStr(ps, "a");
                    string? bHex = GetParamStr(ps, "b");
                    if (aHex == null || bHex == null) return ErrorJson("Missing 'a' or 'b'");
                    Hash? mb = _repo.FindMergeBase(Hash.Parse(aHex), Hash.Parse(bHex));
                    return TransportJson.Serialize(new { @base = mb?.ToHex() ?? "" });
                }
                case "is-ancestor":
                {
                    string? ancHex = GetParamStr(ps, "ancestor");
                    string? desHex = GetParamStr(ps, "descendant");
                    if (ancHex == null || desHex == null) return ErrorJson("Missing ancestor or descendant");
                    bool result = IsAncestorOf(_repo, Hash.Parse(ancHex), Hash.Parse(desHex));
                    return TransportJson.Serialize(new { result });
                }
                case "collect-objects":
                {
                    if (!ps.HasValue) return ErrorJson("Missing params");

                    var wants = new List<Hash>();
                    if (ps.Value.TryGetProperty("wants", out var wa))
                        foreach (JsonElement e in wa.EnumerateArray())
                            if (e.GetString() is string s) wants.Add(Hash.Parse(s));

                    var haves = new List<Hash>();
                    var haveSet = new HashSet<string>();
                    if (ps.Value.TryGetProperty("haves", out var ha))
                    {
                        foreach (JsonElement e in ha.EnumerateArray())
                        {
                            string? s = e.GetString();
                            if (s != null)
                            {
                                haves.Add(Hash.Parse(s));
                                haveSet.Add(s);
                            }
                        }
                    }

                    var result = new List<object>();
                    var visited = new HashSet<string>();
                    var queue = new Queue<Hash>(wants);

                    while (queue.Count > 0)
                    {
                        Hash current = queue.Dequeue();
                        string hex = current.ToHex();
                        if (!visited.Add(hex)) continue;
                        if (haveSet.Contains(hex)) continue;

                        var obj = _repo.Objects.ReadObjectWithType(current);
                        if (!obj.HasValue) continue;

                        result.Add(new { hash = hex, type = obj.Value.type.ToString(), data = Convert.ToBase64String(obj.Value.data) });

                        if (obj.Value.type == ObjectType.Commit)
                        {
                            Commit? commit = ObjectStore.DeserializeCommit(obj.Value.data);
                            if (commit != null)
                            {
                                foreach (Hash p in commit.ParentHashes) queue.Enqueue(p);
                                if (commit.TreeHash.HasValue)
                                    CollectTreeObjectsStdio(commit.TreeHash.Value, result, visited, haveSet);
                            }
                        }
                        else if (obj.Value.type == ObjectType.Tag)
                        {
                            TagObject? tag = ObjectStore.DeserializeTag(obj.Value.data);
                            if (tag != null) queue.Enqueue(tag.TargetHash);
                        }
                    }

                    return TransportJson.Serialize(new { objects = result });
                }
                case "quit":
                {
                    return TransportJson.Serialize(new { ok = true, bye = true });
                }
                default:
                    return ErrorJson($"Unknown method: {method}");
            }
        }
        catch (Exception ex)
        {
            return ErrorJson($"Error: {ex.Message}");
        }
    }

    private void CollectTreeObjectsStdio(Hash treeHash, List<object> result, HashSet<string> visited, HashSet<string> haveSet)
    {
        var queue = new Queue<Hash>();
        queue.Enqueue(treeHash);

        while (queue.Count > 0)
        {
            Hash current = queue.Dequeue();
            string hex = current.ToHex();
            if (!visited.Add(hex)) continue;
            if (haveSet.Contains(hex)) continue;

            var obj = _repo.Objects.ReadObjectWithType(current);
            if (!obj.HasValue) continue;

            result.Add(new { hash = hex, type = obj.Value.type.ToString(), data = Convert.ToBase64String(obj.Value.data) });

            if (obj.Value.type == ObjectType.Tree)
            {
                Tree? tree = ObjectStore.DeserializeTree(obj.Value.data);
                if (tree != null)
                {
                    foreach (var entry in tree.Entries)
                    {
                        string entryHex = entry.ObjectHash.ToHex();
                        if (!visited.Contains(entryHex) && !haveSet.Contains(entryHex))
                        {
                            var entryObj = _repo.Objects.ReadObjectWithType(entry.ObjectHash);
                            if (entryObj.HasValue)
                            {
                                result.Add(new { hash = entryHex, type = entryObj.Value.type.ToString(), data = Convert.ToBase64String(entryObj.Value.data) });
                                visited.Add(entryHex);
                            }
                        }
                        if (entry.Mode == FileMode.Directory)
                            CollectTreeObjectsStdio(entry.ObjectHash, result, visited, haveSet);
                    }
                }
            }
        }
    }

    private static string? GetParamStr(JsonElement? ps, string key)
    {
        if (!ps.HasValue) return null;
        return ps.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static string ErrorJson(string msg) => TransportJson.Serialize(new { error = msg });

    // ─── Static server management ───────────────────────────────────

    public static SmServer? CurrentServer { get; private set; }

    public static async Task StartAsync(string repoPath, string bindAddress, int port, CancellationToken ct = default)
    {
        CurrentServer = new SmServer(repoPath);
        await CurrentServer.RunHttpAsync(bindAddress, port, ct);
    }

    public static async Task StartTcpAsync(string repoPath, string bindAddress, int port, CancellationToken ct = default)
    {
        CurrentServer = new SmServer(repoPath);
        await CurrentServer.RunTcpAsync(bindAddress, port, ct);
    }

    public static async Task StartStdioAsync(string repoPath, CancellationToken ct = default)
    {
        CurrentServer = new SmServer(repoPath);
        await CurrentServer.RunStdioAsync(ct);
    }
}