using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Metrics;
using Kubism.Core.Views;

namespace Kubism.Core.RightSizing;

/// <summary>Acquires bearer tokens for Prometheus-compatible endpoints (e.g. Azure Monitor with an Entra token). Implemented outside Core.</summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct);
}

public interface IPromClient
{
    Task<JsonObject> QueryRangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken ct);
    Task<JsonObject> QueryAsync(string query, DateTimeOffset time, CancellationToken ct);
    Task<string> PingAsync(CancellationToken ct);
}

/// <summary>Prometheus over HTTP, optionally with a bearer token (Azure Monitor managed Prometheus).</summary>
public sealed class HttpPromClient : IPromClient
{
    private readonly HttpClient _http;
    private readonly ITokenProvider? _tokens;
    private readonly string? _scope;

    public HttpPromClient(Uri baseUri, ITokenProvider? tokens = null, string? scope = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        BaseUri = baseUri; _tokens = tokens; _scope = scope;
    }

    public Uri BaseUri { get; }

    private async Task<JsonObject> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, pathAndQuery.TrimStart('/')));
        if (_tokens is not null && _scope is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(_scope, ct).ConfigureAwait(false));
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new KubeApiException((int)res.StatusCode, null, $"Prometheus returned {(int)res.StatusCode}: {Trim(text)}");
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Prometheus returned no JSON object");
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public Task<JsonObject> QueryRangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken ct) =>
        GetAsync($"api/v1/query_range?query={Uri.EscapeDataString(query)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step={(int)step.TotalSeconds}", ct);

    public Task<JsonObject> QueryAsync(string query, DateTimeOffset time, CancellationToken ct) =>
        GetAsync($"api/v1/query?query={Uri.EscapeDataString(query)}&time={time.ToUnixTimeSeconds()}", ct);

    public async Task<string> PingAsync(CancellationToken ct)
    {
        var r = await QueryAsync("vector(1)", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return r["status"]?.ToString() == "success" ? "Connected" : "Unexpected response";
    }
}

/// <summary>Reaches an in-cluster Prometheus through the API server's service proxy.</summary>
public sealed class ProxyPromClient(IKubeTransport transport, string ns, string serviceAndPort) : IPromClient
{
    private string Base => $"/api/v1/namespaces/{ns}/services/{serviceAndPort}/proxy";

    private async Task<JsonObject> GetAsync(string path, CancellationToken ct) =>
        await transport.GetAsync(Base + path, ct).ConfigureAwait(false) as JsonObject ?? throw new InvalidDataException("empty Prometheus response");

    public Task<JsonObject> QueryRangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken ct) =>
        GetAsync($"/api/v1/query_range?query={Uri.EscapeDataString(query)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step={(int)step.TotalSeconds}", ct);

    public Task<JsonObject> QueryAsync(string query, DateTimeOffset time, CancellationToken ct) =>
        GetAsync($"/api/v1/query?query={Uri.EscapeDataString(query)}&time={time.ToUnixTimeSeconds()}", ct);

    public async Task<string> PingAsync(CancellationToken ct)
    {
        var r = await QueryAsync("vector(1)", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return r["status"]?.ToString() == "success" ? "Connected through the API server proxy" : "Unexpected response";
    }
}

/// <summary>cAdvisor / kube-state-metrics history from Prometheus (or Azure Monitor managed Prometheus: same PromQL).</summary>
public sealed class PrometheusUsageSource(IPromClient client, string name = "Prometheus") : IUsageHistorySource
{
    public string Name { get; } = name;

    public Task<string> TestAsync(CancellationToken ct) => client.PingAsync(ct);

    public static string NamespaceMatcher(IReadOnlyList<string>? namespaces) =>
        namespaces is { Count: > 0 } ? $",namespace=~\"{string.Join("|", namespaces.Select(n => System.Text.RegularExpressions.Regex.Escape(n)))}\"" : "";

    public static string Window(TimeSpan w) => w.TotalDays >= 1 && w.TotalDays == Math.Floor(w.TotalDays) ? $"{(int)w.TotalDays}d" : $"{(int)w.TotalHours}h";

    public async Task<UsageHistory> QueryAsync(UsageQuery q, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow; var start = end - q.Window;
        // One batch per namespace keeps responses under the server's sample limits.
        var batches = q.Namespaces is { Count: > 0 } ? q.Namespaces.Select(n => (IReadOnlyList<string>)[n]).ToList() : [null!];
        var points = new Dictionary<ContainerKey, SortedDictionary<long, (double cpu, double mem)>>();
        var extras = new Dictionary<ContainerKey, (double thr, int oom)>();
        foreach (var batch in batches)
        {
            var nsm = NamespaceMatcher(batch);
            var sel = $"{{container!=\"\",container!=\"POD\"{nsm}}}";
            var step = q.Step;
            var rate = $"{(int)Math.Max(step.TotalMinutes, 1)}m";
            var cpu = await client.QueryRangeAsync($"sum by (namespace,pod,container) (rate(container_cpu_usage_seconds_total{sel}[{rate}]))", start, end, step, ct).ConfigureAwait(false);
            var mem = await client.QueryRangeAsync($"max by (namespace,pod,container) (container_memory_working_set_bytes{sel})", start, end, step, ct).ConfigureAwait(false);
            Merge(points, cpu, true); Merge(points, mem, false);
            var w = Window(q.Window);
            var thr = await TryQuery($"avg_over_time((sum by (namespace,pod,container) (rate(container_cpu_cfs_throttled_periods_total{sel}[{rate}])) / sum by (namespace,pod,container) (rate(container_cpu_cfs_periods_total{sel}[{rate}])))[{w}:{rate}])", end, ct).ConfigureAwait(false);
            var oom = await TryQuery($"sum by (namespace,pod,container) (changes(kube_pod_container_status_last_terminated_reason{{reason=\"OOMKilled\"{nsm}}}[{w}]))", end, ct).ConfigureAwait(false);
            foreach (var (k, v) in Instant(thr)) extras[k] = (v, extras.TryGetValue(k, out var e) ? e.oom : 0);
            foreach (var (k, v) in Instant(oom)) extras[k] = (extras.TryGetValue(k, out var e) ? e.thr : 0, (int)v);
        }

        var series = points.Select(kv => new UsageSeries(kv.Key, kv.Value.Select(p => new UsagePoint(DateTimeOffset.FromUnixTimeSeconds(p.Key), p.Value.cpu, p.Value.mem)).ToList())).ToList();
        var first = series.SelectMany(s => s.Points).Select(p => p.Time).DefaultIfEmpty(end).Min();
        var span = end - first;
        return new UsageHistory
        {
            SourceName = Name, Window = q.Window, Series = series,
            Coverage = series.Count == 0 ? 0 : Math.Min(1, span / q.Window), DataHours = span.TotalHours,
            Extras = extras.ToDictionary(kv => kv.Key, kv => new ContainerExtras(kv.Value.thr, kv.Value.oom)),
        };
    }

    private async Task<JsonObject?> TryQuery(string query, DateTimeOffset t, CancellationToken ct)
    {
        try { return await client.QueryAsync(query, t, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // kube-state-metrics or cfs metrics may be absent
    }

    private static void Merge(Dictionary<ContainerKey, SortedDictionary<long, (double cpu, double mem)>> into, JsonObject response, bool isCpu)
    {
        foreach (var r in (response["data"]?["result"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var m = r["metric"] as JsonObject;
            var key = new ContainerKey(m?["namespace"]?.ToString() ?? "", m?["pod"]?.ToString() ?? "", m?["container"]?.ToString() ?? "");
            if (!into.TryGetValue(key, out var d)) into[key] = d = [];
            foreach (var v in (r["values"] as JsonArray ?? []).OfType<JsonArray>())
            {
                var ts = (long)double.Parse(v[0]!.ToString(), CultureInfo.InvariantCulture);
                var val = double.Parse(v[1]!.ToString(), CultureInfo.InvariantCulture);
                if (double.IsNaN(val)) continue;
                d.TryGetValue(ts, out var cur);
                d[ts] = isCpu ? (val, cur.mem) : (cur.cpu, val);
            }
        }
    }

    private static IEnumerable<(ContainerKey, double)> Instant(JsonObject? response)
    {
        foreach (var r in (response?["data"]?["result"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var m = r["metric"] as JsonObject;
            var v = r["value"] as JsonArray;
            if (v is null || !double.TryParse(v[1]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d)) continue;
            yield return (new ContainerKey(m?["namespace"]?.ToString() ?? "", m?["pod"]?.ToString() ?? "", m?["container"]?.ToString() ?? ""), d);
        }
    }
}

/// <summary>VerticalPodAutoscaler recommendations (updateMode Off is enough).</summary>
public sealed class VpaRecommendationSource(IKubeTransport transport) : IUsageHistorySource
{
    public string Name => "VPA recommender";

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var l = await transport.ListAsync(new Gvr("autoscaling.k8s.io", "v1", "verticalpodautoscalers"), null, new ListOptions { Limit = 1 }, ct).ConfigureAwait(false);
        return "VPA available";
    }

    public async Task<UsageHistory> QueryAsync(UsageQuery q, CancellationToken ct)
    {
        var list = await transport.ListAsync(new Gvr("autoscaling.k8s.io", "v1", "verticalpodautoscalers"), null, new ListOptions(), ct).ConfigureAwait(false);
        var vpas = new List<VpaTarget>();
        foreach (var item in list.Items.Select(i => new KubeObject(i)))
        {
            if (q.Namespaces is { Count: > 0 } && !q.Namespaces.Contains(item.Namespace ?? "")) continue;
            var target = item.At("spec", "targetRef") as JsonObject;
            foreach (var cr in (item.At("status", "recommendation", "containerRecommendations") as JsonArray ?? []).OfType<JsonObject>())
            {
                double P(string a, string b) => Quantities.Parse((cr[a] as JsonObject)?[b]?.ToString());
                vpas.Add(new VpaTarget(item.Namespace ?? "", target?["kind"]?.ToString() ?? "", target?["name"]?.ToString() ?? "", cr["containerName"]?.ToString() ?? "",
                    P("target", "cpu"), P("target", "memory"), P("lowerBound", "cpu"), P("upperBound", "cpu"), P("lowerBound", "memory"), P("upperBound", "memory")));
            }
        }
        return new UsageHistory { SourceName = Name, Window = q.Window, Vpa = vpas, Coverage = 0, DataHours = 0, Note = "VPA targets only: no usage series" };
    }
}

/// <summary>Finds an in-cluster Prometheus service (kube-prometheus-stack, prometheus-operated…) reachable through the API server proxy.</summary>
public static class PrometheusDiscovery
{
    private static readonly string[] Excluded = ["alertmanager", "operator", "exporter", "node", "kube-state", "pushgateway", "grafana", "adapter"];

    /// <summary>The service's own port: 9090 when exposed, else a port named web/http, else the first one (GKE and others often change it).</summary>
    public static string PickPort(JsonArray? ports)
    {
        var all = (ports ?? []).OfType<JsonObject>().ToList();
        string? Num(JsonObject p) => p["port"]?.ToString();
        return all.Select(Num).FirstOrDefault(p => p == "9090")
               ?? all.Where(p => p["name"]?.ToString() is "web" or "http" or "http-web" or "prometheus").Select(Num).FirstOrDefault()
               ?? all.Select(Num).FirstOrDefault()
               ?? "9090";
    }

    /// <summary>Parses the cluster setting when it names an in-cluster service instead of a URL: <c>namespace/service:port</c> (port optional, a port name works too).</summary>
    public static bool TryParseServiceRef(string? value, out string ns, out string serviceAndPort)
    {
        ns = serviceAndPort = "";
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://")) return false;
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts[1].StartsWith(':')) return false;
        ns = parts[0]; serviceAndPort = parts[1];
        return true;
    }

    /// <summary>A Prometheus reached through the API server's service proxy, from a <c>namespace/service:port</c> setting.</summary>
    public static IUsageHistorySource FromService(IKubeTransport transport, string ns, string serviceAndPort) =>
        new PrometheusUsageSource(new ProxyPromClient(transport, ns, serviceAndPort), $"Prometheus ({ns}/{serviceAndPort})");

    public static async Task<IUsageHistorySource?> FindAsync(IKubeTransport transport, CancellationToken ct)
    {
        var list = await transport.ListAsync(Gvr.Services, null, new ListOptions(), ct).ConfigureAwait(false);
        foreach (var s in list.Items.Select(i => new KubeObject(i)))
        {
            var n = s.Name;
            if (!n.Contains("prometheus", StringComparison.OrdinalIgnoreCase)) continue;
            if (Excluded.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            var port = PickPort(s.At("spec", "ports") as JsonArray);
            return new PrometheusUsageSource(new ProxyPromClient(transport, s.Namespace ?? "default", $"{n}:{port}"), $"Prometheus ({s.Namespace}/{n})");
        }
        return null;
    }
}
