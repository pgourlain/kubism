using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Events;

namespace Kubism.Core.Security;

/// <summary>
/// Keeps an <see cref="RbacGraph"/> current: one informer each for Roles, ClusterRoles, RoleBindings and ClusterRoleBindings (through the
/// session's <see cref="ResourceCache"/>, so they are shared and reference-counted), and a new graph after every batch of changes.
/// A kind that cannot be read (403) is reported in <see cref="RbacGraph.Availability"/>, never silently left out.
/// </summary>
public sealed class RbacStore : IDisposable
{
    private static readonly (string Resource, string Kind)[] Kinds = [("roles", "Role"), ("clusterroles", "ClusterRole"), ("rolebindings", "RoleBinding"), ("clusterrolebindings", "ClusterRoleBinding")];

    private readonly List<InformerLease> _leases = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    public RbacStore(ResourceCache cache, TimeSpan? debounce = null)
    {
        Debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        foreach (var (resource, _) in Kinds)
        {
            var lease = cache.Subscribe(new Gvr("rbac.authorization.k8s.io", "v1", resource));
            _leases.Add(lease);
            _subscriptions.Add(lease.Informer.Changes.Subscribe(_ => Schedule()));
            lease.Informer.StateChanged += OnState;
        }
        Graph = Rebuild();
    }

    public TimeSpan Debounce { get; }
    public RbacGraph Graph { get; private set; }
    /// <summary>Raised after <see cref="Graph"/> was replaced (never on the caller's thread).</summary>
    public event Action<RbacGraph>? Changed;

    /// <summary>Waits until every informer has listed or failed, so the first graph is as complete as it can be.</summary>
    public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_leases.All(l => l.Informer.IsListed || l.Informer.State == InformerState.Failed)) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        Graph = Rebuild();
        Changed?.Invoke(Graph);
    }

    private void OnState(InformerState _) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                RbacGraph g;
                lock (_gate) { if (_disposed) return; g = Graph = Rebuild(); }
                Changed?.Invoke(g);
            }, null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private RbacGraph Rebuild()
    {
        var sets = new List<KubeObject>[4]; var status = new List<RbacKindStatus>();
        for (var i = 0; i < 4; i++)
        {
            var inf = _leases[i].Informer;
            var kind = Kinds[i].Kind;
            sets[i] = inf.Snapshot().Select(c => Tag(c.Object, kind)).ToList();
            status.Add(new RbacKindStatus(Kinds[i].Resource, inf.IsListed, inf.IsListed ? null : inf.LastError ?? "not loaded yet"));
        }
        return RbacGraph.Build(sets[0], sets[1], sets[2], sets[3], status);
    }

    /// <summary>Items of a Table list can lack kind/apiVersion.</summary>
    private static KubeObject Tag(KubeObject o, string kind)
    {
        if (o.Kind.Length == 0) o.Json["kind"] = kind;
        return o;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _debounce?.Dispose(); }
        foreach (var s in _subscriptions) s.Dispose();
        foreach (var l in _leases) { l.Informer.StateChanged -= OnState; l.Dispose(); }
    }
}
