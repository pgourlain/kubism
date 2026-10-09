using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;

namespace Kubism.Core.Logs;

/// <summary>Logs of every pod of a workload merged by timestamp; pods that appear later join automatically.</summary>
public sealed class WorkloadLogSession : IAsyncDisposable
{
    private readonly IKubeTransport _transport;
    private readonly ResourceCache _cache;
    private readonly string _ns;
    private readonly IReadOnlyDictionary<string, string> _selector;
    private readonly Dictionary<string, LogSession> _sessions = [];
    private readonly object _gate = new();
    private InformerLease? _lease;
    private IDisposable? _sub;
    private CancellationTokenSource? _cts;
    private readonly int? _tail;

    public WorkloadLogSession(IKubeTransport transport, ResourceCache cache, string ns, IReadOnlyDictionary<string, string> selector, LogBuffer buffer, int? tailLines = 200)
    {
        _transport = transport; _cache = cache; _ns = ns; _selector = selector; Buffer = buffer; _tail = tailLines;
    }

    public LogBuffer Buffer { get; }
    public IReadOnlyList<string> Sources { get { lock (_gate) return [.. _sessions.Keys]; } }

    /// <summary>Builds the pod selector from a workload object (spec.selector.matchLabels).</summary>
    public static IReadOnlyDictionary<string, string> SelectorOf(KubeObject workload) =>
        KubeObject.StringMap(workload.At("spec", "selector", "matchLabels"));

    public void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lease = _cache.Subscribe(Gvr.Pods, _ns);
        _sub = Events.ObservableExtensions.Subscribe(_lease.Informer.Changes, _ => Sync());
        Sync();
    }

    private bool Matches(KubeObject pod) => _selector.All(kv => pod.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private void Sync()
    {
        if (_lease is null || _cts is null || _cts.IsCancellationRequested) return;
        var pods = _lease.Informer.Snapshot().Select(i => i.Object).Where(Matches).ToList();
        lock (_gate)
        {
            foreach (var pod in pods)
            {
                var containers = (pod.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>().Select(c => KubeObject.Str(c, "name")!).ToList();
                foreach (var c in containers)
                {
                    var key = $"{pod.Name}/{c}";
                    if (_sessions.ContainsKey(key)) continue;
                    var s = new LogSession(_transport, new LogOptions(_ns, pod.Name, c, true, false, _tail), Buffer, key);
                    _sessions[key] = s;
                    s.Start(_cts.Token);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        _sub?.Dispose();
        _lease?.Dispose();
        List<LogSession> all;
        lock (_gate) { all = [.. _sessions.Values]; _sessions.Clear(); }
        foreach (var s in all) await s.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
