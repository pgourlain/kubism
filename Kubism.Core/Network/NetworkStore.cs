using System.Collections.Concurrent;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Discovery;
using Kubism.Core.Events;
using Kubism.Core.Security;

namespace Kubism.Core.Network;

/// <summary>Looks up the TLS secrets Ingresses name. Only existence and the public certificate's expiry are read; the secret is dropped at once.</summary>
public static class NetworkTls
{
    public static async Task<TlsSecretStatus> LookupAsync(IKubeTransport transport, string ns, string name, CancellationToken ct)
    {
        try
        {
            var node = await transport.GetAsync($"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false) as System.Text.Json.Nodes.JsonObject;
            if (node is null) return new TlsSecretStatus(null, null, "empty answer");
            var cert = CertificateScan.FromTlsSecret(new KubeObject(node));
            return new TlsSecretStatus(true, cert?.NotAfter);
        }
        catch (KubeApiException ex) when (ex.IsNotFound) { return new TlsSecretStatus(false, null); }
        catch (KubeApiException ex) { return new TlsSecretStatus(null, null, ex.IsForbidden ? "forbidden: you cannot read this secret" : ex.Message); }
    }
}

/// <summary>
/// Keeps a <see cref="NetworkGraph"/> current: shared informers for Ingresses, IngressClasses, Services, EndpointSlices and Pods through the session's
/// <see cref="ResourceCache"/>, a new graph after each batch of changes, and the TLS secrets named by Ingresses looked up one by one (cached 5 minutes).
/// A kind that cannot be read is reported in <see cref="NetworkGraph.Availability"/>. Gateway API kinds are read only where the cluster serves them
/// (<c>discovered</c>); the kinds present when the store is created are the ones followed.
/// </summary>
public sealed class NetworkStore : IDisposable
{
    private static readonly (Gvr Gvr, string Name, string Kind)[] Kinds =
    [
        (new("networking.k8s.io", "v1", "ingresses"), "ingresses", "Ingress"), (new("networking.k8s.io", "v1", "ingressclasses"), "ingressclasses", "IngressClass"),
        (new("", "v1", "services"), "services", "Service"), (new("discovery.k8s.io", "v1", "endpointslices"), "endpointslices", "EndpointSlice"), (new("", "v1", "pods"), "pods", "Pod"),
    ];

    // Nodes are optional (a namespaced user often cannot list them): not part of Kinds, so an unreadable node list does not make the whole graph "incomplete".
    private static readonly Gvr NodesGvr = new("", "v1", "nodes");
    private readonly List<InformerLease> _leases = [];
    // Gateway API leases come after the nodes lease in _leases, in the order of this list.
    private readonly List<GatewayKind> _gatewayKinds = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IKubeTransport _transport;
    private readonly ConcurrentDictionary<(string, string), (TlsSecretStatus Status, DateTimeOffset At)> _tls = new();
    private readonly SemaphoreSlim _tlsGate = new(4);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    public NetworkStore(ResourceCache cache, IKubeTransport transport, TimeSpan? debounce = null, IEnumerable<ResourceKind>? discovered = null)
    {
        _transport = transport;
        Debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        foreach (var (gvr, _, _) in Kinds)
        {
            var lease = cache.Subscribe(gvr);
            _leases.Add(lease);
            _subscriptions.Add(lease.Informer.Changes.Subscribe(_ => Schedule()));
            lease.Informer.StateChanged += OnState;
        }
        var nodeLease = cache.Subscribe(NodesGvr);
        _leases.Add(nodeLease);
        _subscriptions.Add(nodeLease.Informer.Changes.Subscribe(_ => Schedule()));
        nodeLease.Informer.StateChanged += OnState;
        foreach (var gk in GatewayApi.Find(discovered ?? []))
        {
            var lease = cache.Subscribe(gk.Gvr);
            _leases.Add(lease); _gatewayKinds.Add(gk);
            _subscriptions.Add(lease.Informer.Changes.Subscribe(_ => Schedule()));
            lease.Informer.StateChanged += OnState;
        }
        Graph = Rebuild();
    }

    public TimeSpan Debounce { get; }
    public TimeSpan TlsTtl { get; init; } = TimeSpan.FromMinutes(5);
    public NetworkGraph Graph { get; private set; }
    public event Action<NetworkGraph>? Changed;

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
                NetworkGraph g;
                lock (_gate) { if (_disposed) return; g = Graph = Rebuild(); }
                Changed?.Invoke(g);
            }, null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private NetworkGraph Rebuild()
    {
        var sets = new List<KubeObject>[Kinds.Length]; var status = new List<RbacKindStatus>();
        for (var i = 0; i < Kinds.Length; i++)
        {
            var inf = _leases[i].Informer;
            sets[i] = inf.Snapshot().Select(c => Tag(c.Object, Kinds[i].Kind)).ToList();
            // The IngressClass list is optional (a cluster with only annotation classes has none, and listing it can be forbidden): reported, but chains still draw.
            status.Add(new RbacKindStatus(Kinds[i].Name, inf.IsListed, inf.IsListed ? null : inf.LastError ?? "not loaded yet"));
        }
        var gatewayObjects = GatewaySnapshot(status);
        var needed = NetworkGraph.TlsSecretsOf(sets[0], gatewayObjects?.Gateways);
        var known = needed.Where(k => _tls.TryGetValue(k, out _)).ToDictionary(k => k, k => _tls[k].Status);
        var nodeInformer = _leases[Kinds.Length].Informer;
        var nodes = nodeInformer.IsListed ? nodeInformer.Snapshot().Select(c => Tag(c.Object, "Node")).ToList() : null;
        var graph = NetworkGraph.Build(sets[0], sets[1], sets[2], sets[3], sets[4], known, status, null, nodes, gatewayObjects);
        foreach (var k in needed) if (!_tls.TryGetValue(k, out var e) || DateTimeOffset.UtcNow - e.At > TlsTtl) _ = FetchTlsAsync(k);
        return graph;
    }

    /// <summary>The Gateway API objects held by the informers (null when the cluster has none), and one availability entry per kind.</summary>
    private GatewayObjects? GatewaySnapshot(List<RbacKindStatus> status)
    {
        if (_gatewayKinds.Count == 0) return null;
        var byKind = new Dictionary<string, List<KubeObject>>();
        for (var i = 0; i < _gatewayKinds.Count; i++)
        {
            var inf = _leases[Kinds.Length + 1 + i].Informer;
            var kind = _gatewayKinds[i].Kind;
            byKind[kind] = inf.Snapshot().Select(c => Tag(c.Object, kind)).ToList();
            status.Add(new RbacKindStatus(_gatewayKinds[i].Name, inf.IsListed, inf.IsListed ? null : inf.LastError ?? "not loaded yet"));
        }
        return GatewayApi.FromKinds(byKind);
    }

    private async Task FetchTlsAsync((string Namespace, string Secret) key)
    {
        // Mark as pending so the same secret is not requested twice while the answer is on its way.
        if (!_tls.TryGetValue(key, out var existing)) _tls[key] = (new TlsSecretStatus(null, null, "checking"), DateTimeOffset.UtcNow);
        else _tls[key] = (existing.Status, DateTimeOffset.UtcNow);
        try
        {
            await _tlsGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var status = await NetworkTls.LookupAsync(_transport, key.Namespace, key.Secret, _cts.Token).ConfigureAwait(false);
                _tls[key] = (status, DateTimeOffset.UtcNow);
            }
            finally { _tlsGate.Release(); }
            Schedule();
        }
        catch (OperationCanceledException) { }
    }

    private static KubeObject Tag(KubeObject o, string kind)
    {
        if (o.Kind.Length == 0) o.Json["kind"] = kind;
        return o;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _debounce?.Dispose(); }
        _cts.Cancel();
        foreach (var s in _subscriptions) s.Dispose();
        foreach (var l in _leases) { l.Informer.StateChanged -= OnState; l.Dispose(); }
    }
}

/// <summary>One-shot graph for callers with no live cache (the MCP server): lists the kinds through the transport and looks up the TLS secrets.</summary>
public static class NetworkLoader
{
    /// <param name="discovered">The kinds the cluster serves: Gateway API is read only for the ones found. Null: Gateway API is not read.</param>
    public static async Task<NetworkGraph> LoadAsync(IKubeTransport transport, CancellationToken ct, bool checkTls = true, IEnumerable<ResourceKind>? discovered = null)
    {
        var sets = new List<KubeObject>[Kinds.Length]; var status = new List<RbacKindStatus>();
        for (var i = 0; i < Kinds.Length; i++)
        {
            sets[i] = [];
            try
            {
                string? cont = null;
                do
                {
                    var page = await transport.ListAsync(Kinds[i].Gvr, null, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        var o = new KubeObject(item);
                        if (o.Kind.Length == 0) item["kind"] = Kinds[i].Kind;
                        sets[i].Add(o);
                    }
                    cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
                } while (cont is not null);
                status.Add(new RbacKindStatus(Kinds[i].Name, true, null));
            }
            catch (Exception ex) when (ex is KubeApiException or KubeConnectionException)
            {
                sets[i] = [];
                status.Add(new RbacKindStatus(Kinds[i].Name, false, ex.Message));
            }
        }
        var gatewayObjects = await LoadGatewayAsync(transport, discovered, status, ct).ConfigureAwait(false);
        var tls = new Dictionary<(string Namespace, string Secret), TlsSecretStatus>();
        if (checkTls)
            foreach (var k in NetworkGraph.TlsSecretsOf(sets[0], gatewayObjects?.Gateways)) tls[k] = await NetworkTls.LookupAsync(transport, k.Namespace, k.Secret, ct).ConfigureAwait(false);
        // Optional: without the right to list nodes, node boxes say their status is unknown.
        List<KubeObject>? nodes = null;
        try
        {
            nodes = [];
            string? cont = null;
            do
            {
                var page = await transport.ListAsync(new Gvr("", "v1", "nodes"), null, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                foreach (var item in page.Items) { var o = new KubeObject(item); if (o.Kind.Length == 0) item["kind"] = "Node"; nodes.Add(o); }
                cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
            } while (cont is not null);
        }
        catch (Exception ex) when (ex is KubeApiException or KubeConnectionException) { nodes = null; }
        return NetworkGraph.Build(sets[0], sets[1], sets[2], sets[3], sets[4], tls, status, null, nodes, gatewayObjects);
    }

    private static async Task<GatewayObjects?> LoadGatewayAsync(IKubeTransport transport, IEnumerable<ResourceKind>? discovered, List<RbacKindStatus> status, CancellationToken ct)
    {
        var kinds = GatewayApi.Find(discovered ?? []);
        if (kinds.Count == 0) return null;
        var byKind = new Dictionary<string, List<KubeObject>>();
        foreach (var gk in kinds)
        {
            var items = new List<KubeObject>();
            try
            {
                string? cont = null;
                do
                {
                    var page = await transport.ListAsync(gk.Gvr, null, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                    foreach (var item in page.Items) { var o = new KubeObject(item); if (o.Kind.Length == 0) item["kind"] = gk.Kind; items.Add(o); }
                    cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
                } while (cont is not null);
                status.Add(new RbacKindStatus(gk.Name, true, null));
            }
            catch (Exception ex) when (ex is KubeApiException or KubeConnectionException)
            {
                items = [];
                status.Add(new RbacKindStatus(gk.Name, false, ex.Message));
            }
            byKind[gk.Kind] = items;
        }
        return GatewayApi.FromKinds(byKind);
    }

    private static readonly (Gvr Gvr, string Name, string Kind)[] Kinds =
    [
        (new("networking.k8s.io", "v1", "ingresses"), "ingresses", "Ingress"), (new("networking.k8s.io", "v1", "ingressclasses"), "ingressclasses", "IngressClass"),
        (new("", "v1", "services"), "services", "Service"), (new("discovery.k8s.io", "v1", "endpointslices"), "endpointslices", "EndpointSlice"), (new("", "v1", "pods"), "pods", "Pod"),
    ];
}
