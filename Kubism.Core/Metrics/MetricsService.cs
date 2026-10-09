using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Core.Metrics;

public sealed record ContainerUsage(string Name, double CpuCores, double MemoryBytes);
public sealed record PodUsage(string Namespace, string Name, IReadOnlyList<ContainerUsage> Containers)
{
    public double CpuCores => Containers.Sum(c => c.CpuCores);
    public double MemoryBytes => Containers.Sum(c => c.MemoryBytes);
}
public sealed record NodeUsage(string Name, double CpuCores, double MemoryBytes);
public readonly record struct UsageSample(DateTimeOffset Time, double Cpu, double Memory);

/// <summary>Polls metrics.k8s.io while someone needs it (ref-counted), keeps 15 minutes of history, reports "not installed" once.</summary>
public sealed class MetricsService : IAsyncDisposable
{
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(15);
    private readonly IKubeTransport _transport;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<UsageSample>> _history = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _refs;
    private bool _reportedMissing;

    public MetricsService(IKubeTransport transport, TimeSpan? interval = null)
    {
        _transport = transport;
        Interval = interval ?? TimeSpan.FromSeconds(15);
    }

    public TimeSpan Interval { get; set; }
    /// <summary>null = unknown yet, false = metrics-server not installed.</summary>
    public bool? Available { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyDictionary<string, PodUsage> Pods { get; private set; } = new Dictionary<string, PodUsage>();
    public IReadOnlyDictionary<string, NodeUsage> Nodes { get; private set; } = new Dictionary<string, NodeUsage>();
    public DateTimeOffset? LastUpdate { get; private set; }
    public event Action? Updated;

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (++_refs == 1)
            {
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(() => RunAsync(ct));
            }
        }
        return new Lease(this);
    }

    private void Release()
    {
        CancellationTokenSource? cts = null;
        lock (_gate) { if (--_refs == 0) { cts = _cts; _cts = null; } }
        try { cts?.Cancel(); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Message = ex.Message; }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var nodes = await _transport.GetAsync(Gvr.NodeMetrics.CollectionPath(), ct).ConfigureAwait(false) as JsonObject;
            var pods = await _transport.GetAsync(Gvr.PodMetrics.CollectionPath(), ct).ConfigureAwait(false) as JsonObject;
            var now = DateTimeOffset.UtcNow;
            var n = ParseNodes(nodes).ToDictionary(x => x.Name);
            var p = ParsePods(pods).ToDictionary(x => $"{x.Namespace}/{x.Name}");
            lock (_gate)
            {
                foreach (var (k, v) in n) Record($"node/{k}", now, v.CpuCores, v.MemoryBytes);
                foreach (var (k, v) in p) Record($"pod/{k}", now, v.CpuCores, v.MemoryBytes);
            }
            Nodes = n; Pods = p; LastLoad(now);
            Available = true; Message = null;
        }
        catch (KubeApiException ex) when (ex.StatusCode is 404 or 503)
        {
            // metrics-server not installed: say it once instead of erroring on every poll.
            Available = false;
            if (!_reportedMissing) { _reportedMissing = true; Message = "metrics-server is not installed in this cluster"; }
            Nodes = new Dictionary<string, NodeUsage>(); Pods = new Dictionary<string, PodUsage>();
        }
        Updated?.Invoke();
    }

    private void LastLoad(DateTimeOffset t) => LastUpdate = t;

    private void Record(string key, DateTimeOffset t, double cpu, double mem)
    {
        if (!_history.TryGetValue(key, out var l)) _history[key] = l = [];
        l.Add(new UsageSample(t, cpu, mem));
        var cutoff = t - HistoryWindow;
        var drop = l.TakeWhile(s => s.Time < cutoff).Count();
        if (drop > 0) l.RemoveRange(0, drop);
    }

    public IReadOnlyList<UsageSample> History(string key)
    {
        lock (_gate) return _history.TryGetValue(key, out var l) ? [.. l] : [];
    }

    public static IEnumerable<PodUsage> ParsePods(JsonObject? list) =>
        (list?["items"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new PodUsage(
            i["metadata"]?["namespace"]?.ToString() ?? "", i["metadata"]?["name"]?.ToString() ?? "",
            (i["containers"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new ContainerUsage(c["name"]?.ToString() ?? "",
                Quantities.ParseCpu(c["usage"]?["cpu"]?.ToString()), Quantities.ParseBytes(c["usage"]?["memory"]?.ToString()))).ToList()));

    public static IEnumerable<NodeUsage> ParseNodes(JsonObject? list) =>
        (list?["items"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new NodeUsage(i["metadata"]?["name"]?.ToString() ?? "",
            Quantities.ParseCpu(i["usage"]?["cpu"]?.ToString()), Quantities.ParseBytes(i["usage"]?["memory"]?.ToString())));

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _cts; _cts = null; _refs = 0; }
        try { cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
    }

    private sealed class Lease(MetricsService s) : IDisposable
    {
        private int _d;
        public void Dispose() { if (Interlocked.Exchange(ref _d, 1) == 0) s.Release(); }
    }
}
