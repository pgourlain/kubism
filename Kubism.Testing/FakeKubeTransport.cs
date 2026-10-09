using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Kubism.Core.Api;
using Kubism.Core.Catalog;

namespace Kubism.Testing;

/// <summary>In-memory IKubeTransport for Core unit tests: scripted lists, injectable watch events and request log.</summary>
public sealed class FakeKubeTransport : IKubeTransport
{
    private readonly object _gate = new();
    private readonly Dictionary<Gvr, List<JsonObject>> _objects = [];
    private readonly Dictionary<string, JsonNode?> _gets = [];
    private readonly List<Channel<WatchEvent>> _watchers = [];
    private long _rv = 100;

    public TimeSpan ClockSkew => TimeSpan.Zero;
    public List<(string method, string path, string? body, string contentType)> Sent { get; } = [];
    public Func<string, string, string?, KubeResponse>? SendHandler { get; set; }
    public Func<Gvr, ListOptions, Exception?>? ListFailure { get; set; }
    public int ListCount { get; private set; }
    public int WatchStarts { get; private set; }
    public List<string?> WatchResourceVersions { get; } = [];
    public Func<Exception?>? WatchFailure { get; set; }
    public bool DisableTables { get; set; }

    public void Set(Gvr gvr, params JsonObject[] objs)
    {
        lock (_gate) _objects[gvr] = [.. objs];
    }

    public void SetGet(string path, JsonNode? node) { lock (_gate) _gets[path] = node; }

    public JsonObject Make(string kind, string name, string? ns = null, string? uid = null, string? apiVersion = "v1")
    {
        var meta = new JsonObject { ["name"] = name, ["uid"] = uid ?? $"uid-{name}", ["resourceVersion"] = (++_rv).ToString() };
        if (ns is not null) meta["namespace"] = ns;
        return new JsonObject { ["apiVersion"] = apiVersion, ["kind"] = kind, ["metadata"] = meta };
    }

    public void Emit(WatchEventType type, JsonObject obj, IReadOnlyList<string?>? cells = null)
    {
        ((JsonObject)obj["metadata"]!)["resourceVersion"] = (++_rv).ToString();
        List<Channel<WatchEvent>> ws;
        lock (_gate) ws = [.. _watchers];
        foreach (var w in ws) w.Writer.TryWrite(new WatchEvent(type, (JsonObject)obj.DeepClone(), cells));
    }

    public void EmitGone()
    {
        List<Channel<WatchEvent>> ws;
        lock (_gate) ws = [.. _watchers];
        var status = new JsonObject { ["kind"] = "Status", ["code"] = 410, ["reason"] = "Expired", ["message"] = "too old resource version" };
        foreach (var w in ws) w.Writer.TryWrite(new WatchEvent(WatchEventType.Error, status));
    }

    public void EndWatches()
    {
        List<Channel<WatchEvent>> ws;
        lock (_gate) { ws = [.. _watchers]; _watchers.Clear(); }
        foreach (var w in ws) w.Writer.TryComplete();
    }

    public int ActiveWatches { get { lock (_gate) return _watchers.Count; } }

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct, string? accept = null)
    {
        lock (_gate)
        {
            if (_gets.TryGetValue(path, out var n)) return Task.FromResult(n?.DeepClone());
        }
        if (path == "/version") return Task.FromResult<JsonNode?>(new JsonObject { ["gitVersion"] = "v1.31.0-fake" });
        throw new KubeApiException(404, "NotFound", $"{path} not found");
    }

    public Task<KubeList> ListAsync(Gvr gvr, string? ns, ListOptions options, CancellationToken ct)
    {
        ListCount++;
        if (ListFailure?.Invoke(gvr, options) is { } ex) throw ex;
        if (options.AsTable && DisableTables) throw new KubeApiException(406, "NotAcceptable", "table not supported");
        List<JsonObject> items;
        lock (_gate) items = _objects.TryGetValue(gvr, out var l) ? l.Where(o => ns is null || o["metadata"]?["namespace"]?.GetValue<string>() == ns).Select(o => (JsonObject)o.DeepClone()).ToList() : [];
        return Task.FromResult(new KubeList
        {
            Items = items, ResourceVersion = _rv.ToString(),
            Columns = options.AsTable ? [new TableColumn("Name", "string", "name", "", 0)] : null,
            Cells = options.AsTable ? items.Select(i => (IReadOnlyList<string?>)[i["metadata"]!["name"]!.GetValue<string>()]).ToList() : null,
        });
    }

    public async IAsyncEnumerable<WatchEvent> WatchAsync(Gvr gvr, string? ns, string? resourceVersion, bool asTable, [EnumeratorCancellation] CancellationToken ct)
    {
        WatchStarts++;
        lock (_gate) WatchResourceVersions.Add(resourceVersion);
        if (WatchFailure?.Invoke() is { } ex) throw ex;
        var ch = Channel.CreateUnbounded<WatchEvent>();
        lock (_gate) _watchers.Add(ch);
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally { lock (_gate) _watchers.Remove(ch); }
    }

    public Task<KubeResponse> SendAsync(string method, string path, string? body, string contentType, CancellationToken ct)
    {
        lock (Sent) Sent.Add((method, path, body, contentType));
        var r = SendHandler?.Invoke(method, path, body) ?? new KubeResponse(200, "{}");
        return Task.FromResult(r);
    }

    public Task<Stream> StreamAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
    public Task<IKubeSocket> OpenSocketAsync(string path, IReadOnlyList<string> subProtocols, CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() { }
}

public sealed class FakeTransportFactory(Func<ClusterEntry, IKubeTransport> create) : IKubeTransportFactory
{
    public IKubeTransport Create(ClusterEntry entry, IExecPluginObserver? observer = null) => create(entry);
}
