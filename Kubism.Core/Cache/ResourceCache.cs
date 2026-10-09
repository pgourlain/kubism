using Kubism.Core.Api;
using Kubism.Core.Events;

namespace Kubism.Core.Cache;

/// <summary>A subscription to an informer. Dispose to release (the informer lingers 60 s, then stops).</summary>
public sealed class InformerLease(Informer informer, IDisposable lease) : IDisposable
{
    public Informer Informer { get; } = informer;
    public void Dispose() => lease.Dispose();
}

/// <summary>One informer per (GVR, namespace-or-all), created on demand and reference-counted.</summary>
public sealed class ResourceCache : IAsyncDisposable
{
    private readonly IKubeTransport _transport;
    private readonly string _clusterId;
    private readonly IClusterEventsService? _events;
    private readonly Dictionary<(Gvr, string), Informer> _informers = [];
    private readonly object _gate = new();
    private bool _lastHealthy = true;

    public ResourceCache(IKubeTransport transport, string clusterId, IClusterEventsService? events = null, InformerOptions? options = null)
    {
        _transport = transport;
        _clusterId = clusterId;
        _events = events;
        Options = options ?? new InformerOptions();
    }

    public InformerOptions Options { get; set; }
    /// <summary>Maximum informers (0 = unlimited): a per-cluster resource budget.</summary>
    public int MaxInformers { get; set; }
    public event Action<bool>? HealthChanged;

    public IReadOnlyList<Informer> Informers
    {
        get { lock (_gate) return [.. _informers.Values]; }
    }

    public InformerLease Subscribe(Gvr gvr, string? ns = null)
    {
        Informer inf;
        lock (_gate)
        {
            var key = (gvr, ns ?? "");
            if (!_informers.TryGetValue(key, out inf!))
            {
                if (MaxInformers > 0 && _informers.Count >= MaxInformers)
                    EvictIdle();
                inf = new Informer(_transport, gvr, ns, Options);
                inf.StateChanged += _ => UpdateHealth();
                inf.Changes.Subscribe(c => _events?.Push(new CacheChanged(_clusterId, gvr, ns, c)));
                inf.Stopped += () => { lock (_gate) { if (inf.RefCount == 0) _informers.Remove(key); } UpdateHealth(); };
                _informers[key] = inf;
            }
        }
        return new InformerLease(inf, inf.Acquire());
    }

    private void EvictIdle()
    {
        foreach (var (k, v) in _informers.Where(p => p.Value.RefCount == 0).ToList())
        {
            _informers.Remove(k);
            _ = v.DisposeAsync().AsTask();
        }
    }

    /// <summary>Degraded when every active informer is failing; healthy again when one is live.</summary>
    private void UpdateHealth()
    {
        bool healthy;
        lock (_gate)
        {
            var active = _informers.Values.Where(i => i.State is not (InformerState.Idle or InformerState.Stopped or InformerState.Listing)).ToList();
            healthy = active.Count == 0 || active.Any(i => i.State == InformerState.Live);
            if (healthy == _lastHealthy) return;
            _lastHealthy = healthy;
        }
        HealthChanged?.Invoke(healthy);
    }

    public async ValueTask DisposeAsync()
    {
        List<Informer> all;
        lock (_gate) { all = [.. _informers.Values]; _informers.Clear(); }
        foreach (var i in all) await i.DisposeAsync().ConfigureAwait(false);
    }
}
