using System.Text.Json.Nodes;

namespace Kubism.Core.Api;

public enum WatchEventType { Added, Modified, Deleted, Bookmark, Error }

/// <param name="Object">The object (or the Status for <see cref="WatchEventType.Error"/>).</param>
/// <param name="Cells">Table cells when the watch was opened with <c>AsTable</c>.</param>
public sealed record WatchEvent(WatchEventType Type, JsonObject Object, IReadOnlyList<string?>? Cells = null)
{
    public string? ResourceVersion => KubeObject.Str(Object["metadata"] as JsonObject ?? new JsonObject(), "resourceVersion");
    public int? ErrorCode => Type == WatchEventType.Error && Object["code"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : null;
}

public sealed record TableColumn(string Name, string Type, string Format, string Description, int Priority);

public sealed class KubeList
{
    public List<JsonObject> Items { get; init; } = [];
    public string? ResourceVersion { get; init; }
    public string? Continue { get; init; }
    /// <summary>Server-side table columns (null when the list is not a Table).</summary>
    public List<TableColumn>? Columns { get; init; }
    /// <summary>Cells per item, parallel to <see cref="Items"/> (null when not a Table).</summary>
    public List<IReadOnlyList<string?>>? Cells { get; init; }
}

public sealed record ListOptions
{
    public int? Limit { get; init; }
    public string? Continue { get; init; }
    public string? LabelSelector { get; init; }
    public string? FieldSelector { get; init; }
    public bool AsTable { get; init; }
}

public sealed record KubeResponse(int StatusCode, string Body)
{
    public JsonNode? Json => string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body);
}

/// <summary>Bidirectional message channel (WebSocket) used by exec, attach and port-forward.</summary>
public interface IKubeSocket : IAsyncDisposable
{
    string? SubProtocol { get; }
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>Receives one binary message, or null when the peer closed.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

/// <summary>The only gateway to a cluster. Implemented by Kubism.Kube; faked in tests.</summary>
public interface IKubeTransport : IDisposable
{
    /// <summary>Server clock minus local clock, measured at connect.</summary>
    TimeSpan ClockSkew { get; }
    Task<JsonNode?> GetAsync(string path, CancellationToken ct, string? accept = null);
    Task<KubeList> ListAsync(Gvr gvr, string? ns, ListOptions options, CancellationToken ct);
    IAsyncEnumerable<WatchEvent> WatchAsync(Gvr gvr, string? ns, string? resourceVersion, bool asTable, CancellationToken ct);
    /// <summary>Raw request. Non-2xx responses are returned, not thrown, except transport failures.</summary>
    Task<KubeResponse> SendAsync(string method, string path, string? body, string contentType, CancellationToken ct);
    Task<Stream> StreamAsync(string path, CancellationToken ct);
    Task<IKubeSocket> OpenSocketAsync(string path, IReadOnlyList<string> subProtocols, CancellationToken ct);
}

/// <summary>Builds a transport for a catalog entry. Implemented in Kubism.Kube.</summary>
public interface IKubeTransportFactory
{
    IKubeTransport Create(Catalog.ClusterEntry entry, IExecPluginObserver? observer = null);
}

/// <summary>Receives the stderr lines of interactive exec auth plugins (device-code login).</summary>
public interface IExecPluginObserver
{
    void OnPluginOutput(string line);
}
