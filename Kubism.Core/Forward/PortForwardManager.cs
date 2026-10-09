using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Events;

namespace Kubism.Core.Forward;

public enum ForwardTargetKind { Pod, Service }
public sealed record ForwardTarget(ForwardTargetKind Kind, string Namespace, string Name, int RemotePort);

public enum ForwardState { Active, Stopped, Error }

/// <summary>A local listener on 127.0.0.1 forwarding to a pod port (or a service resolved to a ready pod).</summary>
public sealed class PortForward : IAsyncDisposable
{
    private readonly PortForwardManager _manager;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _connections;
    private Task? _acceptLoop;

    internal PortForward(PortForwardManager manager, ForwardTarget target, TcpListener listener, bool keep)
    {
        _manager = manager; Target = target; _listener = listener; Keep = keep;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ForwardTarget Target { get; }
    public int LocalPort { get; }
    public bool Keep { get; set; }
    public ForwardState State { get; private set; } = ForwardState.Active;
    public string? LastError { get; private set; }
    public string? ResolvedPod { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _connections);
    public long TotalConnections { get; private set; }
    public string Url => $"http://127.0.0.1:{LocalPort}";

    internal void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError(ex.Message); }
    }

    private void SetError(string message)
    {
        LastError = message;
        _manager.Notify(this);
    }

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _connections); TotalConnections++;
        _manager.Notify(this);
        try
        {
            using var _ = client;
            var (ns, pod, port) = await _manager.ResolveAsync(Target, _cts.Token).ConfigureAwait(false);
            ResolvedPod = pod;
            await using var socket = await _manager.Transport.OpenSocketAsync(
                $"{Gvr.Pods.ObjectPath(ns, pod, "portforward")}?ports={port}", ["v4.channel.k8s.io"], _cts.Token).ConfigureAwait(false);
            LastError = null;
            using var stream = client.GetStream();
            var toRemote = Task.Run(async () =>
            {
                var buf = new byte[32 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buf, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    var msg = new byte[n + 1]; msg[0] = 0; Buffer.BlockCopy(buf, 0, msg, 1, n);
                    await socket.SendAsync(msg, _cts.Token).ConfigureAwait(false);
                }
            }, _cts.Token);
            var fromRemote = Task.Run(async () =>
            {
                var seenData = false; var seenErr = false;
                while (true)
                {
                    var msg = await socket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (msg is null || msg.Length == 0) break;
                    var channel = msg[0];
                    if (channel == 0)
                    {
                        if (!seenData) { seenData = true; if (msg.Length == 3) continue; } // 2-byte port header
                        await stream.WriteAsync(msg.AsMemory(1), _cts.Token).ConfigureAwait(false);
                    }
                    else if (channel == 1)
                    {
                        if (!seenErr) { seenErr = true; if (msg.Length == 3) continue; }
                        var text = System.Text.Encoding.UTF8.GetString(msg, 1, msg.Length - 1);
                        if (text.Length > 0) SetError(text);
                    }
                }
            }, _cts.Token);
            await Task.WhenAny(toRemote, fromRemote).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError(ex.Message); }
        finally
        {
            Interlocked.Decrement(ref _connections);
            _manager.Notify(this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State == ForwardState.Stopped) return;
        State = ForwardState.Stopped;
        try { _cts.Cancel(); } catch { }
        _listener.Stop();
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
        _manager.Notify(this);
    }
}

public sealed class PortForwardManager : IAsyncDisposable
{
    private readonly string _clusterId;
    private readonly IClusterEventsService? _events;
    private readonly List<PortForward> _forwards = [];
    private readonly object _gate = new();

    public PortForwardManager(IKubeTransport transport, string clusterId, IClusterEventsService? events = null)
    {
        Transport = transport; _clusterId = clusterId; _events = events;
    }

    internal IKubeTransport Transport { get; }
    public IReadOnlyList<PortForward> Forwards { get { lock (_gate) return [.. _forwards]; } }
    public event Action? Changed;

    /// <summary>Starts a forward. <paramref name="localPort"/> 0 picks a free port. Call through ClusterActions.StartPortForwardAsync.</summary>
    public PortForward Start(ForwardTarget target, int localPort = 0, bool keep = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start();
        var pf = new PortForward(this, target, listener, keep);
        lock (_gate) _forwards.Add(pf);
        pf.Start();
        Notify(pf);
        return pf;
    }

    public async Task StopAsync(Guid id)
    {
        PortForward? pf;
        lock (_gate) { pf = _forwards.FirstOrDefault(f => f.Id == id); _forwards.Remove(pf!); }
        if (pf is not null) await pf.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    internal void Notify(PortForward pf)
    {
        _events?.Push(new PortForwardChanged(_clusterId, pf.Id));
        Changed?.Invoke();
    }

    /// <summary>Resolves the target to (namespace, pod, port); services pick a ready pod and map the targetPort.</summary>
    internal async Task<(string ns, string pod, int port)> ResolveAsync(ForwardTarget t, CancellationToken ct)
    {
        if (t.Kind == ForwardTargetKind.Pod) return (t.Namespace, t.Name, t.RemotePort);
        var svc = await Transport.GetAsync(Gvr.Services.ObjectPath(t.Namespace, t.Name), ct).ConfigureAwait(false) as JsonObject
                  ?? throw new KubeApiException(404, "NotFound", $"service {t.Name} not found");
        var selector = KubeObject.StringMap(svc["spec"]?["selector"]);
        if (selector.Count == 0) throw new KubeApiException(400, "BadRequest", $"service {t.Name} has no selector");
        var sel = string.Join(",", selector.Select(kv => $"{kv.Key}={kv.Value}"));
        var list = await Transport.ListAsync(Gvr.Pods, t.Namespace, new ListOptions { LabelSelector = sel }, ct).ConfigureAwait(false);
        var pod = list.Items.Select(i => new KubeObject(i)).FirstOrDefault(IsReady)
                  ?? throw new KubeApiException(503, "ServiceUnavailable", $"no ready pod behind service {t.Name}");
        var sp = (svc["spec"]?["ports"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => int.TryParse(p["port"]?.ToString(), out var n) && n == t.RemotePort)
                 ?? throw new KubeApiException(400, "BadRequest", $"service {t.Name} has no port {t.RemotePort}");
        var target = sp["targetPort"]?.ToString();
        int port;
        if (target is null) port = t.RemotePort;
        else if (int.TryParse(target, out var tp)) port = tp;
        else
        {
            // Named port: look it up in the pod's containers.
            port = (pod.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>().SelectMany(c => c["ports"] as JsonArray ?? [])
                .OfType<JsonObject>().Where(p => p["name"]?.ToString() == target).Select(p => int.Parse(p["containerPort"]!.ToString())).FirstOrDefault();
            if (port == 0) throw new KubeApiException(400, "BadRequest", $"named port {target} not found on pod {pod.Name}");
        }
        return (t.Namespace, pod.Name, port);
    }

    private static bool IsReady(KubeObject p) =>
        p.DeletionTimestamp is null && p.StringAt("status", "phase") == "Running" &&
        (p.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().Any(c => c["type"]?.ToString() == "Ready" && c["status"]?.ToString() == "True")
        || (p.StringAt("status", "phase") == "Running" && p.At("status", "conditions") is null && p.DeletionTimestamp is null);

    public async ValueTask DisposeAsync()
    {
        List<PortForward> all;
        lock (_gate) { all = [.. _forwards]; _forwards.Clear(); }
        foreach (var f in all) await f.DisposeAsync().ConfigureAwait(false);
    }
}
