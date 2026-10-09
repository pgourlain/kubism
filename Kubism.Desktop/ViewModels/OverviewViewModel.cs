using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record PhaseCount(string Phase, int Count, Severity Severity);
public sealed record ProblemPod(string Namespace, string Name, string Status, string Age);
public sealed record WarningEvent(string Age, string Object, string Reason, string Message);

/// <summary>Cluster overview: nodes, pods by phase, CPU/memory usage vs capacity vs requests, warnings, problem pods.</summary>
public sealed partial class OverviewViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private readonly List<InformerLease> _leases = [];
    private readonly List<IDisposable> _subs = [];
    private IDisposable? _metrics;
    private int _queued;
    private bool _closed;

    public OverviewViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        foreach (var g in new[] { Gvr.Nodes, Gvr.Pods, Gvr.Events })
        {
            var lease = ws.Session.Cache.Subscribe(g);
            _leases.Add(lease);
            _subs.Add(Kubism.Core.Events.ObservableExtensions.Subscribe(lease.Informer.Changes, _ => Queue()));
        }
        _metrics = ws.Session.Metrics.Acquire();
        ws.Session.Metrics.Updated += Queue;
        Queue();
    }

    [ObservableProperty] private string _version = "";
    [ObservableProperty] private int _nodesReady;
    [ObservableProperty] private int _nodesTotal;
    [ObservableProperty] private double _cpuCapacity;
    [ObservableProperty] private double _cpuRequests;
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _memoryCapacity;
    [ObservableProperty] private double _memoryRequests;
    [ObservableProperty] private double _memoryUsage;
    [ObservableProperty] private bool _metricsAvailable;
    [ObservableProperty] private string? _metricsMessage;
    public ObservableCollection<PhaseCount> Phases { get; } = [];
    public ObservableCollection<ProblemPod> Problems { get; } = [];
    public ObservableCollection<WarningEvent> Warnings { get; } = [];

    public string CpuText => $"{Quantities.FormatCpu(CpuUsage)} used · {Quantities.FormatCpu(CpuRequests)} requested · {Quantities.FormatCpu(CpuCapacity)} allocatable";
    public string MemoryText => $"{Quantities.FormatBytes(MemoryUsage)} used · {Quantities.FormatBytes(MemoryRequests)} requested · {Quantities.FormatBytes(MemoryCapacity)} allocatable";
    public double CpuUsageRatio => CpuCapacity > 0 ? Math.Min(1, CpuUsage / CpuCapacity) : 0;
    public double CpuRequestRatio => CpuCapacity > 0 ? Math.Min(1, CpuRequests / CpuCapacity) : 0;
    public double MemoryUsageRatio => MemoryCapacity > 0 ? Math.Min(1, MemoryUsage / MemoryCapacity) : 0;
    public double MemoryRequestRatio => MemoryCapacity > 0 ? Math.Min(1, MemoryRequests / MemoryCapacity) : 0;

    private void Queue()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = Task.Delay(300).ContinueWith(_ => _ws.Ui.Post(() => { Interlocked.Exchange(ref _queued, 0); Refresh(); }), TaskScheduler.Default);
    }

    public void Refresh()
    {
        if (_closed || _ws.Session.Metrics is null) return;
        var skew = _ws.Session.ClockSkew;
        Version = _ws.Session.ServerVersion ?? "";
        var nodes = _leases[0].Informer.Snapshot().Select(i => new NodeView(i.Object)).ToList();
        NodesTotal = nodes.Count; NodesReady = nodes.Count(n => n.IsReady);
        CpuCapacity = nodes.Sum(n => n.CpuAllocatable); MemoryCapacity = nodes.Sum(n => n.MemoryAllocatable);

        var pods = _leases[1].Informer.Snapshot().Select(i => new PodView(i.Object)).ToList();
        var running = pods.Where(p => p.Phase is "Running" or "Pending").ToList();
        CpuRequests = running.Sum(p => p.Containers.Where(c => !c.IsInit).Sum(c => Quantities.ParseCpu(c.CpuRequest)));
        MemoryRequests = running.Sum(p => p.Containers.Where(c => !c.IsInit).Sum(c => Quantities.ParseBytes(c.MemoryRequest)));
        var m = _ws.Session.Metrics;
        MetricsAvailable = m.Available == true;
        MetricsMessage = m.Available == false ? m.Message : null;
        CpuUsage = m.Nodes.Values.Sum(n => n.CpuCores); MemoryUsage = m.Nodes.Values.Sum(n => n.MemoryBytes);
        foreach (var p in new[] { nameof(CpuText), nameof(MemoryText), nameof(CpuUsageRatio), nameof(CpuRequestRatio), nameof(MemoryUsageRatio), nameof(MemoryRequestRatio) }) OnPropertyChanged(p);

        Phases.Clear();
        foreach (var g in pods.GroupBy(p => p.Phase).OrderByDescending(g => g.Count()))
            Phases.Add(new PhaseCount(g.Key, g.Count(), g.Key switch { "Running" or "Succeeded" => Severity.Ok, "Pending" => Severity.Warning, "Failed" or "Unknown" => Severity.Error, _ => Severity.Neutral }));

        var now = DateTimeOffset.UtcNow + skew;
        Problems.Clear();
        foreach (var p in pods.Where(p => p.IsProblem(now)).OrderBy(p => p.Object.Namespace).ThenBy(p => p.Object.Name).Take(50))
            Problems.Add(new ProblemPod(p.Object.Namespace ?? "", p.Object.Name, p.Status, Core.KubeTime.Age(p.Object.CreationTimestamp, skew)));

        Warnings.Clear();
        foreach (var e in _leases[2].Informer.Snapshot().Select(i => new EventView(i.Object)).Where(e => e.IsWarning).OrderByDescending(e => e.LastSeen).Take(30))
            Warnings.Add(new WarningEvent(Core.KubeTime.Age(e.LastSeen, skew), $"{e.InvolvedKind}/{e.InvolvedName}", e.Reason, e.Message));
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        if (_ws.Session.Metrics is { } mm) mm.Updated -= Queue;
        _metrics?.Dispose();
        foreach (var s in _subs) s.Dispose();
        foreach (var l in _leases) l.Dispose();
        await Task.CompletedTask;
    }
}
