using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.KubeConfigModels;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using WatchEventType = Kubism.Core.Api.WatchEventType;
using Microsoft.Extensions.DependencyInjection;

namespace Kubism.Kube;

/// <summary>KubernetesClient subclass exposing the configured HttpClient and WebSocket connect.</summary>
internal sealed class RawClient(KubernetesClientConfiguration config, DelegatingHandler[] handlers) : Kubernetes(config, handlers)
{
    public HttpClient Http => HttpClient;
    public Task<WebSocket> ConnectAsync(Uri uri, string? subProtocol, CancellationToken ct) => StreamConnectAsync(uri, subProtocol, null, ct);
}

/// <summary>IKubeTransport on top of KubernetesClient's configured HTTP client: raw JSON in, raw JSON out.</summary>
public sealed class KubeTransport : IKubeTransport
{
    private const string TableAccept = "application/json;as=Table;v=v1;g=meta.k8s.io,application/json";
    private readonly RawClient _client;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public KubeTransport(ClusterEntry entry, IExecPluginObserver? observer = null)
    {
        var (config, handlers) = BuildConfig(entry, observer);
        _baseUri = new Uri(config.Host.TrimEnd('/') + "/");
        _client = new RawClient(config, handlers);
        _http = _client.Http;
        AppLog.Info($"Transport for {entry.ContextName} ({_baseUri}), auth: {(handlers.Length > 0 && handlers[0] is ExecAuthHandler ? "exec plugin" : !string.IsNullOrEmpty(config.AccessToken) ? "token" : config.ClientCertificateData is not null || config.ClientCertificateFilePath is not null ? "client certificate" : !string.IsNullOrEmpty(config.Username) ? "basic" : "none")}");
        _http.Timeout = Timeout.InfiniteTimeSpan; // watches and logs are long-lived; callers use cancellation tokens
    }

    public TimeSpan ClockSkew { get; private set; }

    private static (KubernetesClientConfiguration, DelegatingHandler[]) BuildConfig(ClusterEntry entry, IExecPluginObserver? observer)
    {
        var read = KubeConfigReader.Read(entry);
        var handlers = new List<DelegatingHandler>();
        // Exec plugins are run by us: stderr capture for device-code login, refresh before expiry and on 401.
        if (read.Exec is { } spec) handlers.Add(new ExecAuthHandler(new ExecCredentialProvider(spec, observer)));
        // The client library never sends credentials over plain HTTP; the fake API server (tests, --demo) needs them.
        else if (read.Config.Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(read.Config.AccessToken))
            handlers.Add(new StaticBearerHandler(read.Config.AccessToken));
        return (read.Config, [.. handlers]);
    }

    private Uri Url(string path) => new(_baseUri, path.TrimStart('/'));

    // ---------- requests ----------

    private async Task<HttpResponseMessage> RequestAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            var res = await _http.SendAsync(req, option, ct).ConfigureAwait(false);
            // Path only (no query, headers or body): enough to tell which call the server refused.
            if (!res.IsSuccessStatusCode)
            {
                var detail = "";
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // The API server's Status message (e.g. "Unauthorized", token expired, audience) explains the refusal; it holds no secret.
                    try { await res.Content.LoadIntoBufferAsync().ConfigureAwait(false); detail = " body: " + (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false) is { Length: > 300 } b ? b[..300] : await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
                    catch (Exception) { }
                    if (res.Headers.WwwAuthenticate.Count > 0) detail += $" www-authenticate: {res.Headers.WwwAuthenticate}";
                    detail += $" sent-auth: {(req.Headers.Authorization is { } a ? a.Scheme : "none")}";
                }
                AppLog.Warn($"{req.Method} {_baseUri.Host}{req.RequestUri?.AbsolutePath} -> {(int)res.StatusCode} {res.ReasonPhrase}{detail}");
            }
            return res;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"{req.Method} {_baseUri.Host}{req.RequestUri?.AbsolutePath} failed: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            throw MapException(ex);
        }
    }

    public static Exception MapException(Exception ex)
    {
        if (ex is KubeConnectionException or KubeApiException) return ex;
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        return root switch
        {
            AuthenticationException a => new KubeConnectionException($"Certificate not trusted: {a.Message}", ex),
            SocketException s => new KubeConnectionException($"Cannot reach the server: {s.Message}", ex),
            _ when ex is HttpRequestException => new KubeConnectionException($"Cannot reach the server: {root.Message}", ex),
            _ when ex is TaskCanceledException => new KubeConnectionException("The request timed out", ex),
            _ => ex,
        };
    }

    private static async Task<KubeApiException> ToApiException(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
        string? reason = null; var message = body;
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                reason = KubeObject.Str(o, "reason");
                message = KubeObject.Str(o, "message") ?? body;
            }
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(message)) message = $"{(int)r.StatusCode} {r.ReasonPhrase}";
        return new KubeApiException((int)r.StatusCode, reason, message);
    }

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct, string? accept = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
        req.Headers.Accept.ParseAdd(accept ?? "application/json");
        using var res = await RequestAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (path == "/version" && res.Headers.Date is { } date) ClockSkew = date - DateTimeOffset.UtcNow;
        if (!res.IsSuccessStatusCode) throw await ToApiException(res).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<KubeList> ListAsync(Gvr gvr, string? ns, ListOptions options, CancellationToken ct)
    {
        var q = new List<string>();
        if (options.Limit is { } l) q.Add($"limit={l}");
        if (!string.IsNullOrEmpty(options.Continue)) q.Add($"continue={Uri.EscapeDataString(options.Continue)}");
        if (!string.IsNullOrEmpty(options.LabelSelector)) q.Add($"labelSelector={Uri.EscapeDataString(options.LabelSelector)}");
        if (!string.IsNullOrEmpty(options.FieldSelector)) q.Add($"fieldSelector={Uri.EscapeDataString(options.FieldSelector)}");
        if (options.AsTable) q.Add("includeObject=Object");
        var url = gvr.CollectionPath(ns) + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        var node = await GetAsync(url, ct, options.AsTable ? TableAccept : null).ConfigureAwait(false) as JsonObject
                   ?? throw new KubeApiException(500, null, "empty list response");
        return ParseList(node, gvr);
    }

    public static KubeList ParseList(JsonObject node, Gvr gvr)
    {
        var meta = node["metadata"] as JsonObject;
        var rv = meta is null ? null : KubeObject.Str(meta, "resourceVersion");
        var cont = meta is null ? null : KubeObject.Str(meta, "continue");
        if (KubeObject.Str(node, "kind") == "Table")
        {
            var cols = (node["columnDefinitions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => new TableColumn(KubeObject.Str(c, "name") ?? "", KubeObject.Str(c, "type") ?? "string", KubeObject.Str(c, "format") ?? "",
                    KubeObject.Str(c, "description") ?? "", c["priority"]?.GetValue<int>() ?? 0)).ToList();
            var items = new List<JsonObject>(); var cells = new List<IReadOnlyList<string?>>();
            foreach (var row in (node["rows"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var obj = row["object"] as JsonObject;
                if (obj is null) continue;
                obj.Remove("managedFields");
                items.Add(obj.Parent is null ? obj : (JsonObject)obj.DeepClone());
                cells.Add(CellsOf(row));
            }
            return new KubeList { Items = items, Cells = cells, Columns = cols, ResourceVersion = rv, Continue = cont };
        }
        var plain = new List<JsonObject>();
        foreach (var i in (node["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // List items omit kind/apiVersion; restore them so each item is self-describing.
            var kind = KubeObject.Str(node, "kind");
            if (i["kind"] is null && kind is not null && kind.EndsWith("List")) i["kind"] = kind[..^4];
            if (i["apiVersion"] is null) i["apiVersion"] = gvr.ApiVersion;
            plain.Add((JsonObject)i.DeepClone());
        }
        return new KubeList { Items = plain, ResourceVersion = rv, Continue = cont };
    }

    private static IReadOnlyList<string?> CellsOf(JsonObject row) =>
        (row["cells"] as JsonArray ?? []).Select(c => c switch { null => null, JsonValue v when v.TryGetValue<string>(out var s) => s, _ => c.ToJsonString() }).ToList();

    // ---------- watch ----------

    public async IAsyncEnumerable<WatchEvent> WatchAsync(Gvr gvr, string? ns, string? resourceVersion, bool asTable,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var timeout = Random.Shared.Next(300, 600);
        var url = gvr.CollectionPath(ns) + $"?watch=true&allowWatchBookmarks=true&timeoutSeconds={timeout}"
                  + (string.IsNullOrEmpty(resourceVersion) ? "" : $"&resourceVersion={Uri.EscapeDataString(resourceVersion)}")
                  + (asTable ? "&includeObject=Object" : "");
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(url));
        req.Headers.Accept.ParseAdd(asTable ? TableAccept : "application/json");
        using var res = await RequestAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw await ToApiException(res).ConfigureAwait(false);
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var reader = PipeReader.Create(stream);
        try
        {
            while (true)
            {
                ReadResult result;
                try { result = await reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException && (ex is IOException or HttpRequestException or ObjectDisposedException)) { yield break; }
                var buffer = result.Buffer;
                while (TryReadLine(ref buffer, out var line))
                {
                    if (line.IsEmpty) continue;
                    var ev = ParseEvent(line);
                    if (ev is not null) yield return ev;
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) yield break;
            }
        }
        finally { await reader.CompleteAsync().ConfigureAwait(false); }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var pos = buffer.PositionOf((byte)'\n');
        if (pos is null) { line = default; return false; }
        line = buffer.Slice(0, pos.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
        return true;
    }

    public static WatchEvent? ParseEvent(ReadOnlySequence<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (JsonNode.Parse(ref reader) is not JsonObject root) return null;
        var type = KubeObject.Str(root, "type") switch
        {
            "ADDED" => WatchEventType.Added, "MODIFIED" => WatchEventType.Modified, "DELETED" => WatchEventType.Deleted,
            "BOOKMARK" => WatchEventType.Bookmark, "ERROR" => WatchEventType.Error, _ => (WatchEventType?)null,
        };
        if (type is null || root["object"] is not JsonObject obj) return null;
        if (type == WatchEventType.Error) return new WatchEvent(type.Value, obj);
        if (KubeObject.Str(obj, "kind") == "Table")
        {
            var row = (obj["rows"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (row?["object"] is JsonObject inner)
            {
                inner.Remove("managedFields");
                return new WatchEvent(type.Value, (JsonObject)inner.DeepClone(), CellsOf(row));
            }
            // Bookmark delivered as an empty table: keep the resourceVersion.
            return new WatchEvent(WatchEventType.Bookmark, new JsonObject { ["metadata"] = (obj["metadata"] as JsonObject)?.DeepClone() });
        }
        obj.Remove("managedFields");
        return new WatchEvent(type.Value, (JsonObject)obj.DeepClone());
    }

    // ---------- writes, streams, sockets ----------

    public async Task<KubeResponse> SendAsync(string method, string path, string? body, string contentType, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), Url(path));
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/json" : contentType));
        using var res = await RequestAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new KubeResponse((int)res.StatusCode, text);
    }

    public async Task<Stream> StreamAsync(string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
        var res = await RequestAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var ex = await ToApiException(res).ConfigureAwait(false);
            res.Dispose();
            throw ex;
        }
        var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new OwnedStream(s, res);
    }

    public async Task<IKubeSocket> OpenSocketAsync(string path, IReadOnlyList<string> subProtocols, CancellationToken ct)
    {
        var uri = new UriBuilder(Url(path)) { Scheme = _baseUri.Scheme == "https" ? "wss" : "ws" }.Uri;
        Exception? last = null;
        // The client library negotiates one sub-protocol per attempt: try them in preference order.
        foreach (var protocol in subProtocols.Take(2))
        {
            try
            {
                var ws = await _client.ConnectAsync(uri, protocol, ct).ConfigureAwait(false);
                return new WebSocketAdapter(ws);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                var msg = ex.ToString();
                // A definite HTTP error (not found, forbidden…) won't change with another protocol.
                if (msg.Contains("'404'") || msg.Contains("'403'") || msg.Contains("'401'") || msg.Contains("'400'")) break;
            }
        }
        var mapped = MapException(last!);
        if (last!.ToString().Contains("'404'")) throw new KubeApiException(404, "NotFound", "pod or container not found", last);
        if (last.ToString().Contains("'403'")) throw new KubeApiException(403, "Forbidden", "forbidden: you cannot connect to this resource", last);
        throw mapped;
    }

    public void Dispose() => _client.Dispose();

    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
    }
}

internal sealed class StaticBearerHandler(string token) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new("Bearer", token);
        return base.SendAsync(request, ct);
    }
}

internal sealed class WebSocketAdapter(WebSocket ws) : IKubeSocket
{
    public string? SubProtocol => ws.SubProtocol;

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, true, ct).AsTask();

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false); }
            catch (WebSocketException) { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return ms.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token).ConfigureAwait(false);
            }
        }
        catch { }
        ws.Dispose();
    }
}

public sealed class KubeTransportFactory : IKubeTransportFactory
{
    public IKubeTransport Create(ClusterEntry entry, IExecPluginObserver? observer = null) => new KubeTransport(entry, observer);
}

public static class KubeServiceExtensions
{
    public static IServiceCollection AddKubismKube(this IServiceCollection services)
    {
        services.AddSingleton<IKubeTransportFactory, KubeTransportFactory>();
        services.AddSingleton<Kubism.Core.Helm.IHelmRunnerFactory, HelmCliRunnerFactory>();
        return services;
    }
}
