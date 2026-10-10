using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Capacity;
using Kubism.Core.RightSizing;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record NodeRow(NodeCapacity Node)
{
    public string Name => Node.Name;
    public double CpuRequested => Node.CpuRequested;
    public double CpuAllocatable => Node.CpuAllocatable;
    public double MemoryRequested => Node.MemoryRequested;
    public double MemoryAllocatable => Node.MemoryAllocatable;
    public string CpuText => $"{Quantities.FormatCpu(Node.CpuRequested)} / {Quantities.FormatCpu(Node.CpuAllocatable)} ({Node.CpuRatio:P0})";
    public string MemoryText => $"{Quantities.FormatBytes(Node.MemoryRequested)} / {Quantities.FormatBytes(Node.MemoryAllocatable)} ({Node.MemoryRatio:P0})";
    public string PodsText => $"{Node.Pods} / {Node.PodsAllocatable}";
    public string Status => !Node.Ready ? "Not ready" : Node.Unschedulable ? "Cordoned" : "Ready";
    public Severity Severity => !Node.Ready ? Severity.Error : Node.Unschedulable ? Severity.Warning : Math.Max(Node.CpuRatio, Node.MemoryRatio) > 0.9 ? Severity.Warning : Severity.Ok;
}

public sealed record PendingRow(PendingPod Pod)
{
    public string Display => Pod.Display;
    public string Request => $"{Quantities.FormatCpu(Pod.CpuRequest)} · {Quantities.FormatBytes(Pod.MemoryRequest)}";
    public string Reason => Pod.Reason;
    public string Verdict => Pod.Verdict;
}

public sealed record CostRow(CostLine Line, bool Priced)
{
    public string Key => Line.Key;
    public int Pods => Line.Pods;
    public string Cpu => Quantities.FormatCpu(Line.CpuRequested);
    public string Memory => Quantities.FormatBytes(Line.MemoryRequested);
    public string Cost => Line.MonthlyCost is { } c ? c.ToString("N2", CultureInfo.CurrentCulture) : "—";
    public string Share => Line.Share.ToString("P0");
}

public sealed record SimRow(CapacityAnalysis.NamespaceSimulation Ns)
{
    public string Namespace => Ns.Namespace;
    public string Cpu => $"{Quantities.FormatCpu(Ns.CpuNow)} → {Quantities.FormatCpu(Ns.CpuAfter)}";
    public string Memory => $"{Quantities.FormatBytes(Ns.MemoryNow)} → {Quantities.FormatBytes(Ns.MemoryAfter)}";
    public string Saving => Ns.MonthlySaving is { } s ? s.ToString("N2", CultureInfo.CurrentCulture) : "—";
    public Severity Severity => Ns.CpuAfter > Ns.CpuNow ? Severity.Warning : Severity.Neutral;
}

/// <summary>
/// Capacity and cost, from what pods <i>request</i>: how full each node is, the free room and how scattered it is, pending pods that cannot be placed
/// (and whether any node could take them), what each namespace or label costs per month, and what applying the right-sizing recommendations would free.
/// </summary>
public sealed partial class CapacityViewModel : ViewModelBase, IClosable
{
    public const int NodesTab = 0, CostTab = 1, WhatIfTab = 2;
    private readonly WorkspaceViewModel _ws;
    private CancellationTokenSource _cts = new();
    private List<KubeObject> _pods = [];
    private CapacityReport? _capacity;
    private bool _simulated;

    public CapacityViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        var m = ws.Session.Metadata;
        _priceCpu = m.PricePerVcpuHour?.ToString(CultureInfo.InvariantCulture) ?? ""; _priceMemory = m.PricePerGibHour?.ToString(CultureInfo.InvariantCulture) ?? "";
        _ = LoadAsync();
    }

    public ObservableCollection<NodeRow> Nodes { get; } = [];
    public ObservableCollection<PendingRow> Pending { get; } = [];
    public ObservableCollection<CostRow> CostRows { get; } = [];
    public ObservableCollection<SimRow> SimRows { get; } = [];
    public string[] GroupChoices { get; } = ["Namespace", "Label"];
    [ObservableProperty] private int _tab;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSimulating;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _fragmentation = "";
    [ObservableProperty] private string _groupBy = "Namespace";
    [ObservableProperty] private string _labelKey = "app.kubernetes.io/name";
    [ObservableProperty] private string _priceCpu = "";
    [ObservableProperty] private string _priceMemory = "";
    [ObservableProperty] private string _costSummary = "";
    [ObservableProperty] private string _simSummary = "";
    [ObservableProperty] private string _simSource = "";
    [ObservableProperty] private bool _hasPending;

    public bool NeedsPrices => CostRows.Count > 0 && CostRows.All(r => !r.Priced);
    public bool IsLabelGrouping => GroupBy == "Label";

    partial void OnGroupByChanged(string value) { OnPropertyChanged(nameof(IsLabelGrouping)); ShowCost(); }
    partial void OnLabelKeyChanged(string value) => ShowCost();
    partial void OnTabChanged(int value) { if (value == WhatIfTab && !_simulated && !IsSimulating) _ = SimulateAsync(); }

    [RelayCommand]
    public async Task LoadAsync()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken).Token;
        IsLoading = true; Error = null; _simulated = false;
        try
        {
            var disc = _ws.Session.Discovery;
            var nodes = await CompareViewModel.ListAllAsync(_ws, disc.FindByName("nodes") ?? throw new InvalidOperationException("This cluster does not let you list nodes"), null, ct);
            _pods = await CompareViewModel.ListAllAsync(_ws, disc.FindByName("pods") ?? throw new InvalidOperationException("This cluster does not let you list pods"), null, ct);
            _capacity = CapacityAnalysis.Analyze(nodes, _pods);
            ShowNodes();
            ShowCost();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private void ShowNodes()
    {
        var c = _capacity!;
        Nodes.Clear(); foreach (var n in c.Nodes) Nodes.Add(new NodeRow(n));
        Pending.Clear(); foreach (var p in c.Pending) Pending.Add(new PendingRow(p));
        HasPending = Pending.Count > 0;
        Summary = $"{c.Nodes.Count} nodes · {Quantities.FormatCpu(c.CpuRequested)} of {Quantities.FormatCpu(c.CpuAllocatable)} CPU requested ({Ratio(c.CpuRequested, c.CpuAllocatable)}) · {Quantities.FormatBytes(c.MemoryRequested)} of {Quantities.FormatBytes(c.MemoryAllocatable)} memory requested ({Ratio(c.MemoryRequested, c.MemoryAllocatable)})";
        Fragmentation = c.CpuFree <= 0 && c.MemoryFree <= 0 ? "No free room left on the nodes that accept pods"
            : $"Free on schedulable nodes: {Quantities.FormatCpu(c.CpuFree)} CPU and {Quantities.FormatBytes(c.MemoryFree)}; the freest node has {Quantities.FormatCpu(c.LargestFreeCpu)} and {Quantities.FormatBytes(c.LargestFreeMemory)} " +
              $"(fragmentation: CPU {c.CpuFragmentation:P0}, memory {c.MemoryFragmentation:P0}). A pod bigger than the freest node cannot start even if the total looks fine.";
    }

    private static string Ratio(double v, double of) => of <= 0 ? "—" : (v / of).ToString("P0");

    private double? PriceOf(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private void ShowCost()
    {
        if (_capacity is null) return;
        var cpu = PriceOf(PriceCpu); var mem = PriceOf(PriceMemory);
        var key = GroupBy == "Label" ? LabelKey.Trim() : "";
        var lines = CapacityAnalysis.CostBy(_pods, p => key.Length == 0 ? p.Namespace ?? "(cluster)" : p.Labels.GetValueOrDefault(key) ?? "(no " + key + ")", cpu, mem);
        var priced = cpu is not null || mem is not null;
        CostRows.Clear(); foreach (var l in lines) CostRows.Add(new CostRow(l, priced));
        OnPropertyChanged(nameof(NeedsPrices));
        if (!priced) { CostSummary = "Set a price per vCPU-hour and per GiB-hour to see money; the shares below are by CPU requested."; return; }
        var c = _capacity;
        var requested = CapacityAnalysis.MonthlyCost(c.CpuRequested, c.MemoryRequested, cpu, mem);
        var capacity = CapacityAnalysis.MonthlyCost(c.CpuAllocatable, c.MemoryAllocatable, cpu, mem);
        CostSummary = $"Requested {requested:N2} per month of {capacity:N2} for all the nodes' capacity at these prices: {Math.Max(0, capacity - requested):N2} ({(capacity <= 0 ? 0 : 1 - requested / capacity):P0}) is idle.";
    }

    [RelayCommand]
    private void ApplyPrices()
    {
        var meta = _ws.Session.Metadata;
        meta.PricePerVcpuHour = PriceOf(PriceCpu); meta.PricePerGibHour = PriceOf(PriceMemory);
        _ws.Main.Settings.SetMetadata(_ws.Id, meta);
        ShowCost();
        _simulated = false;
    }

    [RelayCommand]
    public async Task SimulateAsync()
    {
        if (_capacity is null) { await LoadAsync(); if (_capacity is null) return; }
        IsSimulating = true; SimSummary = ""; SimSource = ""; SimRows.Clear();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken).Token;
        try
        {
            var errors = new List<string>();
            foreach (var choice in RightSizingRunner.Sources(_ws))
            {
                try
                {
                    var src = await Task.Run(choice.Create, ct);
                    var (report, history) = await RightSizingRunner.RunAsync(_ws, src, 7, null, ct);
                    if (history.Series.Count == 0 && history.Vpa.Count == 0) { errors.Add($"{choice.Name}: no data"); continue; }
                    Show(report, history, choice.Name);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{choice.Name}: {ex.Message}"); }
            }
            SimSummary = "No usage history available to base recommendations on. " + string.Join(" · ", errors.Take(3));
        }
        catch (OperationCanceledException) { }
        finally { IsSimulating = false; }
    }

    private void Show(RightSizingReport report, UsageHistory history, string source)
    {
        var meta = _ws.Session.Metadata;
        var containers = report.Containers.Select(c => new CapacityAnalysis.Container(c.Namespace, Math.Max(1, c.Replicas), c.CpuRequest, c.MemoryRequest,
            c.CpuVerdict == Verdict.NotEnoughData ? null : c.Recommended?.CpuRequest, c.MemoryVerdict == Verdict.NotEnoughData ? null : c.Recommended?.MemoryRequest)).ToList();
        var sim = CapacityAnalysis.Simulate(containers, _capacity!, meta.PricePerVcpuHour, meta.PricePerGibHour);
        _simulated = true;
        SimRows.Clear(); foreach (var n in sim.Namespaces) SimRows.Add(new SimRow(n));
        SimSource = $"{history.SourceName} · {history.Window.TotalDays:0} d window · coverage {history.Coverage:P0}";
        var money = sim.MonthlySaving is { } s ? (s >= 0 ? $", saving ≈ {s:N2} per month" : $", costing ≈ {-s:N2} more per month") : "";
        SimSummary = $"Requests would go from {Quantities.FormatCpu(sim.CpuNow)} CPU / {Quantities.FormatBytes(sim.MemoryNow)} to {Quantities.FormatCpu(sim.CpuAfter)} / {Quantities.FormatBytes(sim.MemoryAfter)} " +
            $"({sim.ContainersChanged} containers changed){money}. The new total would fit on about {sim.NodesNeeded} of the {sim.NodesNow} nodes" + (sim.NodesFreed > 0 ? $": {sim.NodesFreed} could go." : ".");
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
