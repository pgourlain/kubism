using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Capacity;
using Kubism.Core.Drain;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>One node of the Drain page: a checkbox, its pool and how busy it is.</summary>
public sealed partial class DrainNodeRow(string name, string status, string pods, string cpu) : ViewModelBase
{
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string PodsText { get; } = pods;
    public string CpuText { get; } = cpu;
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private bool _isSelected;
}

public sealed record GroupChip(string Name, int Count)
{
    public string Text => $"{Name} ({Count})";
}

/// <summary>
/// The Drain page: choose nodes (one by one or a whole pool), read what would happen to every pod on them, then drain. Choosing and reading never
/// write anything; only <see cref="DrainCommand"/> acts, after a confirmation, through <see cref="WorkspaceViewModel.DrainNodesAsync"/>.
/// </summary>
public sealed partial class DrainViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private DrainSnapshot? _snapshot;
    private HashSet<string> _want;
    private bool _bulk;
    private bool _closed;

    public DrainViewModel(WorkspaceViewModel ws, IReadOnlyList<string>? preselect)
    {
        _ws = ws;
        _want = preselect?.ToHashSet(StringComparer.Ordinal) ?? [];
        Plan.PropertyChanged += OnPlanChanged;
        _ = LoadAsync();
    }

    public DrainPlanViewModel Plan { get; } = new();
    public ObservableCollection<DrainNodeRow> Nodes { get; } = [];
    public ObservableCollection<GroupChip> Groups { get; } = [];
    public ObservableCollection<string> GroupLabels { get; } = [];

    [ObservableProperty] private string? _groupLabel;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isDraining;

    public bool NoGroupLabel => GroupLabels.Count == 0 && !IsLoading;
    public string SelectionText => SelectedCount == 0 ? "No node selected" : $"{SelectedCount} node{(SelectedCount == 1 ? "" : "s")} selected";
    public string DrainLabel => SelectedCount == 0 ? "Cordon and drain…" : SelectedCount == 1 ? "Cordon and drain this node…" : $"Cordon and drain {SelectedCount} nodes…";
    public bool CanDrain => SelectedCount > 0 && Plan.CanDrain && !IsLoading && !IsDraining;

    public IReadOnlyList<string> SelectedNames => Nodes.Where(n => n.IsSelected).Select(n => n.Name).ToList();

    partial void OnGroupLabelChanged(string? value) => Regroup();
    partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(NoGroupLabel)); Notify(); }
    partial void OnIsDrainingChanged(bool value) => Notify();

    private void OnPlanChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DrainPlanViewModel.CanDrain)) Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanDrain));
        DrainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ticks these nodes (a node action sent the user here); the others keep their tick.</summary>
    public void Select(IReadOnlyList<string> names)
    {
        foreach (var n in names) _want.Add(n);
        _bulk = true;
        foreach (var row in Nodes) if (names.Contains(row.Name)) row.IsSelected = true;
        _bulk = false;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectGroup(string? name)
    {
        _bulk = true;
        foreach (var row in Nodes) if (row.Group == name) row.IsSelected = true;
        _bulk = false;
        UpdateSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _bulk = true;
        foreach (var row in Nodes) row.IsSelected = false;
        _bulk = false;
        UpdateSelection();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        if (_closed) return;
        IsLoading = true; Error = null;
        try
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken);
            var snapshot = await DrainPreflightLoader.LoadAsync(_ws.Session.Transport, link.Token);
            if (_closed) return;
            Apply(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private void Apply(DrainSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var n in SelectedNames) _want.Add(n);   // a refresh keeps what the user ticked
        var caps = CapacityAnalysis.Analyze(snapshot.Nodes, snapshot.Pods).Nodes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _bulk = true;
        Nodes.Clear();
        foreach (var n in snapshot.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            var cap = caps.GetValueOrDefault(n.Name);
            var row = new DrainNodeRow(n.Name, new NodeView(n).Status, cap is null ? "" : $"{cap.Pods} pods", cap is null ? "" : $"CPU {cap.CpuRatio:P0}")
            { IsSelected = _want.Contains(n.Name) };
            row.PropertyChanged += OnRowChanged;
            Nodes.Add(row);
        }
        GroupLabels.Clear();
        foreach (var l in Scheduling.GroupLabelsPresent(snapshot.Nodes)) GroupLabels.Add(l);
        if (GroupLabel is null || !GroupLabels.Contains(GroupLabel)) GroupLabel = GroupLabels.FirstOrDefault();
        Regroup();
        _bulk = false;
        OnPropertyChanged(nameof(NoGroupLabel));
        UpdateSelection();
    }

    private void Regroup()
    {
        if (_snapshot is null) return;
        var byName = _snapshot.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
        foreach (var row in Nodes) row.Group = GroupLabel is null ? "" : Scheduling.NodeGroup(byName[row.Name], GroupLabel) ?? "";
        Groups.Clear();
        foreach (var g in Nodes.Where(n => n.Group.Length > 0).GroupBy(n => n.Group).OrderBy(g => g.Key, StringComparer.Ordinal)) Groups.Add(new GroupChip(g.Key, g.Count()));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DrainNodeRow.IsSelected) && !_bulk) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var names = SelectedNames;
        _want = names.ToHashSet(StringComparer.Ordinal);
        SelectedCount = names.Count;
        OnPropertyChanged(nameof(SelectionText)); OnPropertyChanged(nameof(DrainLabel));
        Plan.Set(_snapshot, names);
        Notify();
    }

    /// <summary>The only action of the page. It asks for a confirmation (and, in production, the typed name) before it writes anything.</summary>
    [RelayCommand(CanExecute = nameof(CanDrain))]
    private async Task Drain()
    {
        var names = SelectedNames;
        IsDraining = true;
        try
        {
            if (await _ws.DrainNodesAsync(names, Plan.Options, Plan.Plan)) await LoadAsync();
        }
        finally { IsDraining = false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
