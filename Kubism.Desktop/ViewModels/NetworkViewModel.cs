using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Network;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>
/// The Network graph page: how traffic reaches the pods, from what is exposed outside the cluster through Ingresses and Services to workloads, with the
/// ports of every hop and what does not line up. Read-only. Follows the cluster live (<see cref="NetworkStore"/>) and uses the workspace's namespace
/// selection (the same picker as the lists); the controller of another namespace stays drawn as context so a path is never cut.
/// </summary>
public sealed partial class NetworkViewModel : ViewModelBase, IClosable
{
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly NetworkStore? _store;
    private NetworkGraph _graph = NetworkGraph.Build([], [], [], [], []);

    public NetworkViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        try { _store = new NetworkStore(ws.Session.Cache, ws.Session.Transport); }
        catch (Exception ex) { Error = ex.Message; IsLoading = false; return; }
        _store.Changed += g => _ws.Ui.Post(() => Apply(g));
        ws.Namespaces.Changed += OnNamespacesChanged;
        _ = LoadAsync();
    }

    public WorkspaceViewModel Workspace => _ws;
    public IReadOnlyList<string> ExposureChoices { get; } = ["Any exposure", "Outside the cluster only", "In-cluster only"];
    public ObservableCollection<string> NodeLines { get; } = [];

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private NetworkView? _view;
    [ObservableProperty] private PlacedNode? _selectedNode;
    [ObservableProperty] private string _selectedExposure = "Any exposure";
    [ObservableProperty] private bool _problemsOnly;
    [ObservableProperty] private bool _includeInternal;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _viewSummary = "";

    public NetworkGraph Graph => _graph;
    public bool HasWarning => Warning is not null;
    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnSelectedExposureChanged(string value) => Rebuild();
    partial void OnProblemsOnlyChanged(bool value) => Rebuild();
    partial void OnIncludeInternalChanged(bool value) => Rebuild();
    partial void OnSearchChanged(string value) => Rebuild();

    private void OnNamespacesChanged() => _ws.Ui.Post(Rebuild);

    private async Task LoadAsync()
    {
        try
        {
            await _store!.WaitReadyAsync(TimeSpan.FromSeconds(15), _cts.Token);
            _ws.Ui.Post(() => { Apply(_store.Graph); IsLoading = false; });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ws.Ui.Post(() => { Error = ex.Message; IsLoading = false; }); }
    }

    private void Apply(NetworkGraph g)
    {
        _graph = g;
        Warning = g.IncompleteWarning;
        var ingresses = g.Nodes.Count(n => n.Kind == NetworkNodeKind.Ingress);
        var outside = g.Nodes.Count(n => n.Kind == NetworkNodeKind.Ingress && n.Exposure == ExposureLevel.Outside);
        Summary = $"{ingresses} ingress(es), {outside} outside the cluster · {g.Nodes.Count(n => n.Kind == NetworkNodeKind.Service)} services · {g.Nodes.Count(n => n.Kind == NetworkNodeKind.Workload)} workloads"
                  + " · 'Outside the cluster' is judged from Services and ingress controllers; DNS, firewalls and WAFs are not visible.";
        Rebuild();
    }

    private void Rebuild()
    {
        var ns = _ws.Namespaces.IsAll ? null : (IReadOnlySet<string>)_ws.Namespaces.Selected.ToHashSet();
        var filter = new NetworkFilter
        {
            Namespaces = ns,
            Exposure = SelectedExposure switch { "Outside the cluster only" => ExposureFilter.OutsideOnly, "In-cluster only" => ExposureFilter.InClusterOnly, _ => ExposureFilter.Any },
            ProblemsOnly = ProblemsOnly, IncludeInternalServices = IncludeInternal, Search = string.IsNullOrWhiteSpace(Search) ? null : Search,
        };
        var v = _graph.View(filter);
        var keep = SelectedNode?.Id;
        View = v;
        SelectedNode = v.Nodes.FirstOrDefault(n => n.Id == keep);
        ViewSummary = v.Nodes.Count == 0
            ? "Nothing to show with these filters (Services nothing routes to are hidden unless you tick 'Show internal services')."
            : $"{v.Chains} chain(s) · {v.OutsideChains} outside the cluster · {v.ProblemChains} with problems" + (v.Truncated ? " · first chains only, narrow the filters" : "");
        ShowNode();
    }

    [RelayCommand]
    private void NodeClicked(PlacedNode? placed)
    {
        if (placed is null) return;
        SelectedNode = placed;
        ShowNode();
        // The object itself opens in the standard details panel (YAML, events…).
        var n = placed.Node;
        if (n.Missing) return;
        if (n.Kind == NetworkNodeKind.Workload && n.Id.Split('/') is { Length: 3 } parts && n.Namespace is { } wns)
            _ws.OpenGraphNode(new GraphNode(n.Id, parts[1], wns, parts[2]));
        else if (n.Source is { Kind.Length: > 0 } src)
            _ws.OpenGraphNode(new GraphNode(n.Id, src.Kind, src.Namespace, src.Name));
    }

    private void ShowNode()
    {
        NodeLines.Clear();
        if (SelectedNode?.Node is not { } n) return;
        NodeLines.Add($"{n.KindText}: {n.Label}{(n.Namespace is null ? " (outside the cluster)" : " · namespace " + n.Namespace)}");
        NodeLines.Add($"exposure: {n.Exposure switch { ExposureLevel.Outside => "outside the cluster", ExposureLevel.Unknown => "unknown", _ => "in-cluster only" }}");
        if (n.Detail.Length > 0) NodeLines.Add(n.Detail);
        foreach (var l in n.Lines) NodeLines.Add(l);
        foreach (var p in n.Problems) NodeLines.Add($"⚠ {p.Description}");
    }

    /// <summary>Hosts, paths and ports of what is on screen, as text: for a ticket or a message.</summary>
    [RelayCommand]
    private Task CopyDetails()
    {
        var text = NodeLines.Count > 0 ? string.Join("\n", NodeLines)
            : string.Join("\n", View?.Nodes.Where(p => p.Node.Kind == NetworkNodeKind.Ingress).SelectMany(p => p.Node.Lines.Where(l => !l.StartsWith("class:") && !l.StartsWith("TLS")).Select(l => $"{p.Node.Namespace}/{p.Node.Label}: {l}")) ?? []);
        return _ws.Clipboard.SetTextAsync(text);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _ws.Namespaces.Changed -= OnNamespacesChanged;
        _store?.Dispose();
        return ValueTask.CompletedTask;
    }
}
