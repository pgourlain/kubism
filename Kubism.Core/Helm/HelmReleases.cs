using System.IO.Compression;
using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Helm;

public sealed record HelmRelease(string Name, string Namespace, int Revision, string Status, string Chart, string ChartVersion, string? AppVersion,
    DateTimeOffset? Updated, string? Description, string? Notes, JsonObject? UserValues, JsonObject? ChartValues, string? Manifest)
{
    public string Key => $"{Namespace}/{Name}";
    /// <summary>User values merged over chart defaults (what helm calls "computed values").</summary>
    public JsonObject ComputedValues => (JsonObject)(DeepMerge(ChartValues ?? new JsonObject(), UserValues ?? new JsonObject()) ?? new JsonObject());

    private static JsonNode? DeepMerge(JsonNode? a, JsonNode? b)
    {
        if (b is JsonObject bo && a is JsonObject ao)
        {
            var r = (JsonObject)ao.DeepClone();
            foreach (var kv in bo) r[kv.Key] = DeepMerge(r[kv.Key], kv.Value);
            return r;
        }
        return b?.DeepClone();
    }
}

/// <summary>Reads Helm 3 releases from their storage secrets (helm.sh/release.v1): base64 → gzip → JSON. No helm binary needed.</summary>
public sealed class HelmReleases(IKubeTransport transport)
{
    public async Task<IReadOnlyList<HelmRelease>> ListAllRevisionsAsync(string? ns, CancellationToken ct)
    {
        var list = await transport.ListAsync(Gvr.Secrets, ns, new ListOptions { LabelSelector = "owner=helm", FieldSelector = "type=helm.sh/release.v1" }, ct).ConfigureAwait(false);
        var releases = new List<HelmRelease>();
        foreach (var item in list.Items)
        {
            var o = new KubeObject(item);
            if (o.StringAt("type") is { } t && t != "helm.sh/release.v1") continue;
            try { if (Decode(o) is { } r) releases.Add(r); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Text.Json.JsonException) { /* skip unreadable revisions */ }
        }
        return releases;
    }

    /// <summary>Latest revision of every release.</summary>
    public async Task<IReadOnlyList<HelmRelease>> ListAsync(string? ns, CancellationToken ct) =>
        (await ListAllRevisionsAsync(ns, ct).ConfigureAwait(false)).GroupBy(r => r.Key).Select(g => g.OrderByDescending(r => r.Revision).First())
        .OrderBy(r => r.Namespace).ThenBy(r => r.Name).ToList();

    public async Task<IReadOnlyList<HelmRelease>> HistoryAsync(string ns, string name, CancellationToken ct) =>
        (await ListAllRevisionsAsync(ns, ct).ConfigureAwait(false)).Where(r => r.Namespace == ns && r.Name == name).OrderBy(r => r.Revision).ToList();

    /// <summary>The raw <c>chart</c> object of one revision (templates, values, files, metadata), read from its release secret.</summary>
    public async Task<JsonObject?> GetChartAsync(string ns, string name, int revision, CancellationToken ct)
    {
        var node = await transport.GetAsync(Gvr.Secrets.ObjectPath(ns, $"sh.helm.release.v1.{name}.v{revision}"), ct).ConfigureAwait(false) as JsonObject;
        return node is null ? null : DecodeJson(new KubeObject(node))?["chart"] as JsonObject;
    }

    public static HelmRelease? Decode(KubeObject secret) => DecodeJson(secret) is { } json ? FromJson(json) : null;

    public static JsonObject? DecodeJson(KubeObject secret)
    {
        var data = secret.At("data", "release")?.ToString();
        if (string.IsNullOrEmpty(data)) return null;
        var helmB64 = Convert.FromBase64String(data);          // Kubernetes secret encoding
        var gz = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(helmB64)); // helm's own base64
        using var ms = new MemoryStream(gz);
        Stream s = ms;
        if (gz.Length > 2 && gz[0] == 0x1f && gz[1] == 0x8b) s = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(s);
        var json = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidDataException("release is not an object");
        return json;
    }

    public static HelmRelease FromJson(JsonObject r)
    {
        var info = r["info"] as JsonObject; var chart = r["chart"] as JsonObject; var meta = chart?["metadata"] as JsonObject;
        return new HelmRelease(
            r["name"]?.ToString() ?? "", r["namespace"]?.ToString() ?? "", int.TryParse(r["version"]?.ToString(), out var v) ? v : 0,
            info?["status"]?.ToString() ?? "unknown", meta?["name"]?.ToString() ?? "", meta?["version"]?.ToString() ?? "", meta?["appVersion"]?.ToString(),
            DateTimeOffset.TryParse(info?["last_deployed"]?.ToString(), out var d) ? d : null, info?["description"]?.ToString(), info?["notes"]?.ToString(),
            r["config"] as JsonObject, chart?["values"] as JsonObject, r["manifest"]?.ToString());
    }
}

public interface IHelmRunner
{
    bool IsAvailable { get; }
    Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

public interface IHelmRunnerFactory
{
    IHelmRunner Create(Catalog.ClusterEntry entry);
}
