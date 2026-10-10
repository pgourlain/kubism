using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Security;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>One cell of the verbs × resources matrix. Risky cells are drawn with an error border.</summary>
public sealed record CellVm(string Text, string Tip, Severity Severity, bool Risky);

public sealed record MatrixRowVm(string Resource, bool Risky, IReadOnlyList<CellVm> Cells);

public sealed record PathRow(RbacPath Path)
{
    public string Binding => Path.Binding.Display;
    public string Role => Path.Role.Display;
    public string Rule => Path.Rule.Summary;
    public string Scope => Path.ScopeText;
    public string Via => Path.ViaGroup is null ? "" : $"group {Path.ViaGroup}";
    public string Note => Path.Note ?? "";
}

public sealed record WhoCanRow(WhoCanEntry Entry)
{
    public string Subject => Entry.Subject.Display;
    public string Kind => Entry.Subject.Kind;
    public string Paths => Entry.Paths.Count == 0 ? "built in (not RBAC)" : $"{Entry.Paths.Count} path(s): {Entry.Paths[0].Binding.Display} → {Entry.Paths[0].Role.Display}";
    public string Risks => string.Join(", ", Entry.Risks.Select(r => r.Code));
    public Severity Severity => Entry.Risks.Any(r => r.Severity == FindingSeverity.High) || Entry.IsBroad ? Severity.Error : Entry.Risks.Count > 0 ? Severity.Warning : Severity.Neutral;
    public string Breadth => Entry.IsBroad ? "everyone" : "";
}

/// <summary>
/// The RBAC graph page: how rights are given, three ways. <b>Chain</b>: subject → binding → role → rules, with scope and a RoleBinding → ClusterRole
/// marked. <b>Subject</b>: what can X do, as a verbs × resources matrix per namespace plus the paths. <b>Resource</b>: who can do this, with the
/// binding + role + rule behind each answer. Read-only: nothing here writes to the cluster. The graph follows the RBAC objects live
/// (<see cref="RbacStore"/>) and says so when some kinds could not be read.
/// </summary>
public sealed partial class RbacViewModel : ViewModelBase, IClosable
{
    public const int ChainTab = 0, SubjectTab = 1, ResourceTab = 2;
    private const string AllNamespaces = "All namespaces", ClusterWide = "(cluster-wide)";
    private static readonly string[] StdVerbs = ["get", "list", "watch", "create", "update", "patch", "delete", "deletecollection"];

    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly RbacStore? _store;
    private readonly RbacVerifier _verifier;
    private RbacGraph _graph = RbacGraph.Build([], [], [], []);
    private RbacSubject? _subject;
    private IReadOnlyList<string> _extraGroups = [];
    private bool _suppress;

    public RbacViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        _verifier = new RbacVerifier(ws.Session.Transport, ws.Session.Access);
        try { _store = new RbacStore(ws.Session.Cache); }
        catch (Exception ex) { Error = ex.Message; return; }
        _store.Changed += g => _ws.Ui.Post(() => Apply(g));
        _ = LoadAsync();
    }

    public ObservableCollection<string> NamespaceChoices { get; } = [AllNamespaces];
    public IReadOnlyList<string> KindChoices { get; } = ["All subjects", RbacSubject.User, RbacSubject.Group, RbacSubject.ServiceAccount];
    public ObservableCollection<string> KnownSubjects { get; } = [];
    public ObservableCollection<string> SubjectNamespaces { get; } = [];
    public ObservableCollection<string> ResourceNamespaces { get; } = [ClusterWide];
    public IReadOnlyList<string> Verbs { get; } = StdVerbs;
    public ObservableCollection<string> MatrixVerbs { get; } = [];
    public ObservableCollection<MatrixRowVm> MatrixRows { get; } = [];
    public ObservableCollection<PathRow> SubjectPaths { get; } = [];
    public ObservableCollection<string> SubjectNotes { get; } = [];
    public ObservableCollection<WhoCanRow> WhoCanRows { get; } = [];
    public ObservableCollection<PathRow> WhoPaths { get; } = [];
    public ObservableCollection<string> NodeLines { get; } = [];

    [ObservableProperty] private int _tab;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string _summary = "";

    // chain
    [ObservableProperty] private RbacChain? _chain;
    [ObservableProperty] private RbacChainNode? _selectedNode;
    [ObservableProperty] private string _selectedNamespace = AllNamespaces;
    [ObservableProperty] private string _selectedKind = "All subjects";
    [ObservableProperty] private bool _riskyOnly;
    [ObservableProperty] private string _subjectSearch = "";
    [ObservableProperty] private string _chainSummary = "";

    // subject
    [ObservableProperty] private string _subjectText = "";
    [ObservableProperty] private string? _selectedSubjectNamespace;
    [ObservableProperty] private string _subjectSummary = "";

    // resource
    [ObservableProperty] private string _verb = "get";
    [ObservableProperty] private string _resourceText = "secrets";
    [ObservableProperty] private string _resourceNamespace = ClusterWide;
    [ObservableProperty] private string _resourceName = "";
    [ObservableProperty] private WhoCanRow? _selectedWho;
    [ObservableProperty] private string _whoSummary = "";
    [ObservableProperty] private string _verifyText = "";
    [ObservableProperty] private bool _canReviewOthers;
    [ObservableProperty] private string _reviewOthersTip = "Checking whether you may create subjectaccessreviews…";

    public RbacGraph Graph => _graph;
    public bool HasWarning => Warning is not null;
    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedNamespaceChanged(string value) => RebuildChain();
    partial void OnSelectedKindChanged(string value) => RebuildChain();
    partial void OnRiskyOnlyChanged(bool value) => RebuildChain();
    partial void OnSubjectSearchChanged(string value) => RebuildChain();
    partial void OnSelectedSubjectNamespaceChanged(string? value) { if (!_suppress) ShowMatrix(); }
    partial void OnSelectedWhoChanged(WhoCanRow? value)
    {
        WhoPaths.Clear();
        if (value is not null) foreach (var p in value.Entry.Paths) WhoPaths.Add(new PathRow(p));
    }

    private async Task LoadAsync()
    {
        try
        {
            await _store!.WaitReadyAsync(TimeSpan.FromSeconds(15), _cts.Token);
            _ws.Ui.Post(() => { Apply(_store.Graph); IsLoading = false; });
            var can = await _verifier.CanReviewOthersAsync(_cts.Token);
            _ws.Ui.Post(() => { CanReviewOthers = can.Allowed; ReviewOthersTip = can.Allowed ? "Ask the API server about the selected subject (SubjectAccessReview)" : can.Reason ?? "forbidden: you cannot create subjectaccessreviews"; });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ws.Ui.Post(() => { Error = ex.Message; IsLoading = false; }); }
    }

    /// <summary>A new graph arrived (first load, or an RBAC object changed): redraw whatever is on screen.</summary>
    private void Apply(RbacGraph g)
    {
        _graph = g;
        Warning = g.IncompleteWarning;
        Summary = $"{g.ClusterRoles.Count + g.Roles.Count} roles · {g.Bindings.Count} bindings · {g.KnownSubjects().Count} subjects" + (g.OrphanBindings.Count > 0 ? $" · {g.OrphanBindings.Count} binding(s) to a missing role" : "");
        SyncChoices(NamespaceChoices, AllNamespaces, g.Bindings.Select(b => b.Namespace).Concat(g.Roles.Select(r => r.Namespace)));
        SyncChoices(ResourceNamespaces, ClusterWide, g.Bindings.Select(b => b.Namespace).Concat(g.Roles.Select(r => r.Namespace)));
        KnownSubjects.Clear();
        foreach (var s in g.KnownSubjects()) KnownSubjects.Add(s.Display);
        RebuildChain();
        if (_subject is not null) ShowSubject();
        if (WhoCanRows.Count > 0) Search();
    }

    private void SyncChoices(ObservableCollection<string> target, string first, IEnumerable<string?> namespaces)
    {
        var wanted = namespaces.Where(n => n is not null).Select(n => n!).Distinct().Order(StringComparer.Ordinal).Prepend(first).ToList();
        if (target.SequenceEqual(wanted)) return;
        // Edit in place: clearing the collection would reset the ComboBox selection and push null back into the view model.
        foreach (var gone in target.Where(t => !wanted.Contains(t)).ToList()) target.Remove(gone);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= target.Count || target[i] != wanted[i]) target.Insert(i, wanted[i]);
    }

    // ---------- chain ----------

    private void RebuildChain()
    {
        var filter = new RbacChainFilter
        {
            Namespace = SelectedNamespace == AllNamespaces ? null : SelectedNamespace,
            SubjectKinds = SelectedKind == "All subjects" ? null : new HashSet<string> { SelectedKind },
            RiskyOnly = RiskyOnly,
            SubjectQuery = string.IsNullOrWhiteSpace(SubjectSearch) ? null : SubjectSearch,
        };
        var chain = RbacChain.Build(_graph, filter);
        var keep = SelectedNode?.Id;
        Chain = chain;
        SelectedNode = chain.Nodes.FirstOrDefault(n => n.Id == keep);
        var bindings = chain.Nodes.Count(n => n.Kind == RbacNodeKind.Binding);
        ChainSummary = chain.Nodes.Count == 0 ? "Nothing to show with these filters." : $"{bindings} binding(s) · {chain.Nodes.Count(n => n.Kind == RbacNodeKind.Subject)} subject(s) · {chain.Nodes.Count(n => n.Risky)} risky"
            + (chain.Truncated ? " · first 150 bindings only, narrow the filters" : "");
        ShowNode();
    }

    [RelayCommand]
    private void NodeClicked(RbacChainNode? node)
    {
        if (node is null) return;
        if (node.Kind == RbacNodeKind.Subject && node.Subject is { } s) { SelectSubject(s); return; }
        SelectedNode = node;
        ShowNode();
        // The RBAC object itself opens in the standard details panel (YAML, events…).
        if (node.Source is { Kind.Length: > 0 } src && node.Kind is RbacNodeKind.Binding or RbacNodeKind.Role && !node.Missing)
            _ws.OpenGraphNode(new GraphNode(node.Id, src.Kind, src.Namespace, src.Name));
    }

    private void ShowNode()
    {
        NodeLines.Clear();
        if (SelectedNode is not { } n) return;
        NodeLines.Add($"{n.Detail}: {n.Label} · {n.ScopeText}");
        foreach (var r in n.Risks) NodeLines.Add($"⚠ {r.Description}");
        foreach (var rule in n.Rules) NodeLines.Add("• " + rule.Summary);
        if (n.Missing) NodeLines.Add("This role does not exist: the binding grants nothing.");
    }

    // ---------- subject ----------

    /// <summary>Jump to the subject-centred view for this subject (from a graph click, a who-can row or the search box).</summary>
    public void SelectSubject(RbacSubject subject, IReadOnlyList<string>? groups = null)
    {
        _subject = subject; _extraGroups = groups ?? [];
        SubjectText = subject.Display;
        Tab = SubjectTab;
        ShowSubject();
    }

    [RelayCommand]
    private void ShowSubject()
    {
        _subject = RbacSubject.Parse(SubjectText) ?? _subject;
        SubjectPaths.Clear(); SubjectNotes.Clear(); MatrixRows.Clear(); MatrixVerbs.Clear();
        if (_subject is null) { SubjectSummary = "Type a subject: User name, Group name or ServiceAccount namespace/name."; return; }
        var rights = _graph.RightsOf(_subject, _extraGroups);
        foreach (var p in rights.Paths) SubjectPaths.Add(new PathRow(p));
        foreach (var n in rights.Notes) SubjectNotes.Add(n);
        _suppress = true;
        var keep = SelectedSubjectNamespace;
        SubjectNamespaces.Clear(); SubjectNamespaces.Add(ClusterWide);
        foreach (var n in rights.Namespaces) SubjectNamespaces.Add(n);
        SelectedSubjectNamespace = keep is not null && SubjectNamespaces.Contains(keep) ? keep : rights.Namespaces.Count > 0 ? rights.Namespaces[0] : ClusterWide;
        _suppress = false;
        SubjectSummary = rights.Paths.Count == 0
            ? $"{_subject.Display} holds no right through RBAC beyond what every user gets."
            : $"{_subject.Display}: {rights.Paths.Count} rule(s) through {rights.Paths.Select(p => p.Binding.Key).Distinct().Count()} binding(s)" + (_extraGroups.Count > 0 ? $" · groups: {string.Join(", ", _extraGroups)}" : "");
        ShowMatrix();
    }

    private void ShowMatrix()
    {
        MatrixRows.Clear(); MatrixVerbs.Clear();
        if (_subject is null || SelectedSubjectNamespace is not { } ns) return;
        var m = _graph.MatrixFor(_subject, ns == ClusterWide ? null : ns, _extraGroups);
        foreach (var v in m.Verbs) MatrixVerbs.Add(v);
        foreach (var row in m.Rows)
            MatrixRows.Add(new MatrixRowVm(row.Resource, row.Risky, row.Cells.Select(c => new CellVm(
                c.Level switch { RightLevel.Allowed => "✔", RightLevel.Limited => "◐", _ => "" },
                c.Level == RightLevel.None ? "" : $"{(c.Level == RightLevel.Limited ? "only named objects · " : "")}{c.Via}{(c.Risky ? " · risky" : "")}",
                c.Risky ? Severity.Error : c.Level == RightLevel.Allowed ? Severity.Ok : c.Level == RightLevel.Limited ? Severity.Warning : Severity.Neutral,
                c.Risky)).ToList()));
    }

    /// <summary>Use the identity the API server sees for the current user (SelfSubjectReview), with its groups.</summary>
    [RelayCommand]
    private async Task UseMeAsync()
    {
        try
        {
            var id = await _verifier.WhoAmIAsync(_cts.Token);
            if (id is null) { SubjectSummary = "Your identity could not be read (SelfSubjectReview needs Kubernetes 1.28+)."; return; }
            _ws.Ui.Post(() => SelectSubject(id.Subject, id.Groups));
        }
        catch (OperationCanceledException) { }
    }

    // ---------- resource ----------

    [RelayCommand]
    private void Search()
    {
        WhoCanRows.Clear(); WhoPaths.Clear();
        if (string.IsNullOrWhiteSpace(ResourceText)) { WhoSummary = "Type a resource, e.g. secrets, pods/exec, deployments.apps, or a path such as /metrics."; return; }
        var req = Request();
        var result = _graph.WhoCan(req);
        foreach (var e in result) WhoCanRows.Add(new WhoCanRow(e));
        WhoSummary = $"Who can {req.Display}: {result.Count} subject(s)" + (_graph.IsComplete ? "" : " · INCOMPLETE data, see the warning");
        SelectedWho = null;
    }

    private RbacRequest Request() =>
        RbacRequest.Parse(Verb.Trim(), ResourceText.Trim(), ResourceNamespace == ClusterWide ? null : ResourceNamespace, string.IsNullOrWhiteSpace(ResourceName) ? null : ResourceName.Trim());

    [RelayCommand]
    private async Task VerifyMeAsync()
    {
        try
        {
            var v = await _verifier.VerifySelfAsync(_graph, Request(), _cts.Token);
            _ws.Ui.Post(() => VerifyText = Describe(v, "you"));
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task VerifySelectedAsync()
    {
        if (SelectedWho is null) return;
        try
        {
            var v = await _verifier.VerifySubjectAsync(_graph, SelectedWho.Entry.Subject, Request(), _cts.Token);
            _ws.Ui.Post(() => VerifyText = Describe(v, SelectedWho?.Subject ?? "subject"));
        }
        catch (OperationCanceledException) { }
    }

    private static string Describe(RbacVerification v, string who) =>
        $"{who}: RBAC objects say {Word(v.Computed)}, API server says {Word(v.Server)}. {v.Note}{(string.IsNullOrEmpty(v.Reason) ? "" : $" ({v.Reason})")}";

    private static string Word(bool? b) => b is null ? "unknown" : b.Value ? "allowed" : "denied";

    [RelayCommand]
    private void ShowResourceFor(WhoCanRow? row) { if (row is not null) SelectSubject(row.Entry.Subject); }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _store?.Dispose();
        return ValueTask.CompletedTask;
    }
}
