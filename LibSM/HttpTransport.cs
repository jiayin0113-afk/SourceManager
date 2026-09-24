using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SourceManager;

public class HttpTransport : Transport
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _remoteName;
    private readonly string? _token;

    public HttpTransport(string url, string? remoteName)
    {
        _remoteName = remoteName;

        // Pull any credentials out of the URL so they do not end up in logs or error messages.
        var parsed = new Uri(url);
        string? userInfo = null;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            userInfo = Uri.UnescapeDataString(parsed.UserInfo);
            var builder = new UriBuilder(parsed) { UserName = "", Password = "" };
            parsed = builder.Uri;
        }
        _baseUrl = parsed.ToString().TrimEnd('/');

        // Credentials are resolved once, here, and sent as a bearer token on every request.
        _token = ResolveToken(userInfo);

        var handler = new HttpClientHandler();

        // Certificate validation is ON by default. A self-signed or internal-CA server must be
        // opted into explicitly, per host, rather than by trusting every certificate presented
        // to us — which is what the previous unconditional `return true` did.
        string? insecureHost = Environment.GetEnvironmentVariable("SM_INSECURE_HOST");
        if (!string.IsNullOrEmpty(insecureHost) &&
            parsed.Host.Equals(insecureHost, StringComparison.OrdinalIgnoreCase))
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            SmLog.WriteWarning($"TLS certificate validation is DISABLED for {parsed.Host} (SM_INSECURE_HOST).");
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };

        if (_token != null)
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    /// <summary>
    /// Token resolution order: credentials embedded in the URL, then SM_TOKEN, then
    /// SM_TOKEN_&lt;REMOTE&gt; for per-remote tokens.
    /// </summary>
    private string? ResolveToken(string? userInfo)
    {
        if (!string.IsNullOrEmpty(userInfo)) return userInfo;

        if (!string.IsNullOrEmpty(_remoteName))
        {
            string? perRemote = Environment.GetEnvironmentVariable(
                $"SM_TOKEN_{_remoteName.ToUpperInvariant().Replace('-', '_')}");
            if (!string.IsNullOrEmpty(perRemote)) return perRemote;
        }

        string? global = Environment.GetEnvironmentVariable("SM_TOKEN");
        return string.IsNullOrEmpty(global) ? null : global;
    }

    private async Task<T?> GetAsync<T>(string endpoint) where T : class
    {
        try
        {
            var resp = await _http.GetAsync(endpoint);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<T>(TransportJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string endpoint)
    {
        try
        {
            var resp = await _http.GetAsync(endpoint);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(json)) return null;
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task<T?> PostAsync<T>(string endpoint, object? body = null)
    {
        try
        {
            HttpResponseMessage resp;
            if (body != null)
            {
                var content = new StringContent(TransportJson.Serialize(body), Encoding.UTF8, "application/json");
                resp = await _http.PostAsync(endpoint, content);
            }
            else
            {
                resp = await _http.PostAsync(endpoint, new StringContent("{}", Encoding.UTF8, "application/json"));
            }
            resp.EnsureSuccessStatusCode();
            string text = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(text)) return default;
            return JsonSerializer.Deserialize<T>(text, TransportJson.Options);
        }
        catch (Exception ex)
        {
            SmLog.WriteError($"HTTP POST {endpoint}: {ex.Message}");
            return default;
        }
    }

    private async Task<string?> PostStringAsync(string endpoint, object? body = null)
    {
        try
        {
            HttpResponseMessage resp;
            if (body != null)
            {
                var content = new StringContent(TransportJson.Serialize(body), Encoding.UTF8, "application/json");
                resp = await _http.PostAsync(endpoint, content);
            }
            else
            {
                resp = await _http.PostAsync(endpoint, new StringContent("{}", Encoding.UTF8, "application/json"));
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> PostBytesAsync(string endpoint, byte[] data)
    {
        try
        {
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var resp = await _http.PostAsync(endpoint, content);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> DelAsync(string endpoint)
    {
        try
        {
            var resp = await _http.DeleteAsync(endpoint);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─── Refs ───────────────────────────────────────────────────────

    public override Hash? GetHeadCommit()
    {
        var dict = GetAsync<Dictionary<string, string>>(TransportProtocol.HeadEndpoint).Result;
        if (dict == null) return null;
        dict.TryGetValue("hash", out string? hex);
        if (hex == null) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    public override string? GetCurrentBranch()
    {
        var dict = GetAsync<Dictionary<string, string>>(TransportProtocol.HeadEndpoint).Result;
        if (dict == null) return null;
        dict.TryGetValue("branch", out string? branch);
        return branch;
    }

    public override List<BranchInfo> ListBranches()
    {
        var result = new List<BranchInfo>();
        JsonDocument? doc = GetJsonAsync(TransportProtocol.BranchesEndpoint).Result;
        if (doc == null) return result;

        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in root.EnumerateArray())
            {
                result.Add(BranchInfo.FromJson(elem.GetRawText()));
            }
        }
        return result;
    }

    public override Hash? GetBranch(string branchName)
    {
        var dict = GetAsync<Dictionary<string, string>>($"{TransportProtocol.BranchesEndpoint}/{Uri.EscapeDataString(branchName)}").Result;
        if (dict == null) return null;
        dict.TryGetValue("hash", out string? hex);
        if (hex == null) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    public override void SetBranch(string branchName, Hash hash, string? message = null)
    {
        PostAsync<object>($"{TransportProtocol.BranchesEndpoint}/{Uri.EscapeDataString(branchName)}", new
        {
            hash = hash.ToHex(),
            message = message ?? ""
        }).Wait();
    }

    public override void CreateBranch(string branchName, Hash hash)
    {
        PostAsync<object>($"{TransportProtocol.BranchesEndpoint}/create", new
        {
            name = branchName,
            hash = hash.ToHex()
        }).Wait();
    }

    public override void DeleteBranch(string branchName)
    {
        DelAsync($"{TransportProtocol.BranchesEndpoint}/{Uri.EscapeDataString(branchName)}").Wait();
    }

    public override List<string> ListTags()
    {
        var result = new List<string>();
        JsonDocument? doc = GetJsonAsync(TransportProtocol.TagsEndpoint).Result;
        if (doc == null) return result;

        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in root.EnumerateArray())
            {
                result.Add(elem.GetString() ?? "");
            }
        }
        return result;
    }

    public override Hash? GetTag(string tagName)
    {
        var dict = GetAsync<Dictionary<string, string>>($"{TransportProtocol.TagsEndpoint}/{Uri.EscapeDataString(tagName)}").Result;
        if (dict == null) return null;
        dict.TryGetValue("hash", out string? hex);
        if (hex == null) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    public override void SetTag(string tagName, Hash hash, string? message = null)
    {
        PostAsync<object>($"{TransportProtocol.TagsEndpoint}/{Uri.EscapeDataString(tagName)}", new
        {
            hash = hash.ToHex(),
            message = message ?? ""
        }).Wait();
    }

    public override void DeleteTag(string tagName)
    {
        DelAsync($"{TransportProtocol.TagsEndpoint}/{Uri.EscapeDataString(tagName)}").Wait();
    }

    public override Hash? GetRemoteBranch(string remoteName, string branchName)
    {
        var dict = GetAsync<Dictionary<string, string>>(
            $"{TransportProtocol.RemotesEndpoint}/{Uri.EscapeDataString(remoteName)}/{Uri.EscapeDataString(branchName)}").Result;
        if (dict == null) return null;
        dict.TryGetValue("hash", out string? hex);
        if (hex == null) return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    public override void SetRemoteBranch(string remoteName, string branchName, Hash hash)
    {
        PostAsync<object>($"{TransportProtocol.RemotesEndpoint}/{Uri.EscapeDataString(remoteName)}/{Uri.EscapeDataString(branchName)}", new
        {
            hash = hash.ToHex(),
            message = $"fetch: {remoteName}/{branchName}"
        }).Wait();
    }

    public override void DeleteRemoteBranch(string remoteName, string branchName)
    {
        DelAsync($"{TransportProtocol.RemotesEndpoint}/{Uri.EscapeDataString(remoteName)}/{Uri.EscapeDataString(branchName)}").Wait();
    }

    public override List<(string remote, string branch, Hash hash)> ListRemoteBranches(string? remoteName = null)
    {
        var result = new List<(string remote, string branch, Hash hash)>();
        string ep = remoteName != null
            ? $"{TransportProtocol.RemotesEndpoint}/{Uri.EscapeDataString(remoteName)}"
            : TransportProtocol.RemotesEndpoint;

        JsonDocument? doc = GetJsonAsync(ep).Result;
        if (doc == null) return result;

        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in root.EnumerateArray())
            {
                string rn = elem.GetProperty("remote").GetString() ?? "";
                string bn = elem.GetProperty("branch").GetString() ?? "";
                string hx = elem.GetProperty("hash").GetString() ?? "";
                try
                {
                    result.Add((rn, bn, Hash.Parse(hx)));
                }
                catch { }
            }
        }
        return result;
    }

    // ─── Objects ────────────────────────────────────────────────────

    public override byte[]? ReadObjectRaw(Hash hash)
    {
        try
        {
            return _http.GetByteArrayAsync($"{TransportProtocol.ObjectsEndpoint}/{hash.ToHex()}").Result;
        }
        catch
        {
            return null;
        }
    }

    public override (ObjectType type, byte[] compressed)? ReadObjectWithType(Hash hash)
    {
        byte[]? raw = ReadObjectRaw(hash);
        if (raw == null) return null;
        var metadata = GetAsync<Dictionary<string, string>>($"{TransportProtocol.ObjectsEndpoint}/{hash.ToHex()}/meta").Result;
        ObjectType type = ObjectType.Blob;
        if (metadata != null && metadata.TryGetValue("type", out string? typeStr))
        {
            try { type = Enum.Parse<ObjectType>(typeStr, true); }
            catch { }
        }
        return (type, raw);
    }

    public override bool Exists(Hash hash)
    {
        try
        {
            var resp = _http.GetAsync($"{TransportProtocol.ObjectsCheckEndpoint}/{hash.ToHex()}").Result;
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public override void WriteObjectRaw(Hash hash, byte[] compressedData, ObjectType type)
    {
        var payload = TransportJson.Serialize(new
        {
            hash = hash.ToHex(),
            type = type.ToString(),
            data = Convert.ToBase64String(compressedData)
        });

        PostAsync<object>($"{TransportProtocol.ObjectsEndpoint}/{hash.ToHex()}", new
        {
            type = type.ToString(),
            data = Convert.ToBase64String(compressedData)
        }).Wait();
    }

    public override void WriteObjectsBatch(Dictionary<Hash, (ObjectType type, byte[] data)> objects)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var (hash, (type, data)) in objects)
        {
            list.Add(new Dictionary<string, string>
            {
                ["hash"] = hash.ToHex(),
                ["type"] = type.ToString(),
                ["data"] = Convert.ToBase64String(data)
            });
        }
        PostAsync<object>(TransportProtocol.ObjectsBatchEndpoint, new { objects = list }).Wait();
    }

    // ─── Operations ─────────────────────────────────────────────────

    public override bool IsRepository()
    {
        try
        {
            var resp = _http.GetAsync(TransportProtocol.IsRepoEndpoint).Result;
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public override Hash? FindMergeBase(Hash a, Hash b)
    {
        var result = PostAsync<Dictionary<string, string>>(TransportProtocol.MergeBaseEndpoint, new
        {
            a = a.ToHex(),
            b = b.ToHex()
        }).Result;
        if (result == null || !result.TryGetValue("base", out string? hex) || string.IsNullOrEmpty(hex))
            return null;
        try { return Hash.Parse(hex); } catch { return null; }
    }

    public override bool IsAncestor(Hash ancestor, Hash descendant)
    {
        JsonDocument? doc = PostAsync<JsonDocument>(TransportProtocol.AncestorEndpoint, new
        {
            ancestor = ancestor.ToHex(),
            descendant = descendant.ToHex()
        }).Result;

        if (doc == null) return false;
        return doc.RootElement.TryGetProperty("result", out JsonElement res) && res.GetBoolean();
    }

    public override Dictionary<Hash, (ObjectType type, byte[] data)> CollectObjects(List<Hash> wants, List<Hash> haves)
    {
        var result = new Dictionary<Hash, (ObjectType type, byte[] data)>();

        var response = PostStringAsync(TransportProtocol.CollectObjectsEndpoint, new
        {
            wants = wants.Select(w => w.ToHex()).ToList(),
            haves = haves.Select(h => h.ToHex()).ToList()
        }).Result;

        if (response == null) return result;

        JsonDocument? doc;
        try { doc = JsonDocument.Parse(response); }
        catch { return result; }

        if (doc == null) return result;

        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in root.EnumerateArray())
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
        _http.Dispose();
    }
}