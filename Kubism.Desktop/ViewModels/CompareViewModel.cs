using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Discovery;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public enum DiffLineKind { Context, Added, Removed, Gap }

public sealed record DiffLine(string Text, DiffLineKind Kind)
{
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsGap => Kind == DiffLineKind.Gap;
}

/// <summary>A kind present in both clusters (the group and resource decide, each side keeps its own served version).</summary>
public sealed record CompareKind(ResourceKind Left, ResourceKind Right)
{
    public string Title => Left.Group.Length == 0 ? Left.Kind : $"{Left.Kind} ({Left.Group})";
    public override string ToString() => Title;
}

public sealed record CompareRow(CompareEntry Entry)
{
    public string Display => Entry.Display;
    public string Marker => Entry.Status switch { CompareStatus.Same => "=", CompareStatus.Different => "≠", CompareStatus.OnlyLeft => "◀", _ => "▶" };
    public string StatusText => Entry.Status switch { CompareStatus.Same => "identical", CompareStatus.Different => $"+{Entry.Added} −{Entry.Removed}", CompareStatus.OnlyLeft => "only here", _ => "only there" };
    public Severity Severity => Entry.Status switch { CompareStatus.Same => Severity.Ok, CompareStatus.Different => Severity.Warning, _ => Severity.Error };
}

/// <summary>
/// Same kind of object in two clusters (or two namespaces): what exists on one side only and what differs, with a line diff
/// of the manifests once server-generated fields are dropped. Secret values are never shown, only whether a key differs.
/// </summary>
public sealed partial class CompareViewModel : ViewModelBase, IClosable
{
    private static readonly string[] Preferred = ["deployments", "statefulsets", "daemonsets", "services", "configmaps", "secrets", "ingresses", "cronjobs", "horizontalpodautoscalers", "networkpolicies", "serviceaccounts", "roles", "rolebindings", "persistentvolumeclaims", "pods"];
    private const string AllNamespaces = "(all namespaces)";
    private readonly WorkspaceViewModel _ws;
    private CancellationTokenSource? _cts;
    private List<CompareRow> _all = [];

    public CompareViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        Targets = ws.Main.Workspaces;
        _target = Targets.FirstOrDefault(w => w != ws) ?? ws;
        LeftNamespaces = [AllNamespaces, .. NamespacesOf(ws)];
        _leftNamespace = ws.Namespaces.Selected.Count == 1 ? ws.Namespaces.Selected[0] : AllNamespaces;
        RebuildTarget();
        _rightNamespace = LeftNamespaces.Contains(_leftNamespace) && RightNamespaces.Contains(_leftNamespace) ? _leftNamespace : AllNamespaces;
        Kind = Kinds.FirstOrDefault();
    }

    public ObservableCollection<WorkspaceViewModel> Targets { get; }
    public ObservableCollection<CompareKind> Kinds { get; } = [];
    public IReadOnlyList<string> LeftNamespaces { get; }
    [ObservableProperty] private IReadOnlyList<string> _rightNamespaces = [];
    public ObservableCollection<CompareRow> Rows { get; } = [];
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    [ObservableProperty] private WorkspaceViewModel _target;
    [ObservableProperty] private CompareKind? _kind;
    [ObservableProperty] private string _leftNamespace;
    [ObservableProperty] private string _rightNamespace;
    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private bool _showIdentical;
    [ObservableProperty] private CompareRow? _selected;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _diffTitle = "";

    public string LeftTitle => _ws.Title;
    public string RightTitle => Target.Title;

    private static IEnumerable<string> NamespacesOf(WorkspaceViewModel w) => w.Namespaces.Choices.Select(c => c.Name).Order(StringComparer.Ordinal);

    partial void OnTargetChanged(WorkspaceViewModel value)
    {
        RebuildTarget();
        RightNamespace = RightNamespaces.Contains(LeftNamespace) ? LeftNamespace : AllNamespaces;
        OnPropertyChanged(nameof(RightTitle));
    }

    private void RebuildTarget()
    {
        RightNamespaces = [AllNamespaces, .. NamespacesOf(Target)];
        var previous = Kind?.Title;
        Kinds.Clear();
        var right = Target.Session.Discovery.Kinds.Where(k => k.CanList).ToDictionary(k => (k.Group, k.Resource), k => k, EqualityComparer<(string, string)>.Default);
        var common = _ws.Session.Discovery.Kinds.Where(k => k.CanList && right.ContainsKey((k.Group, k.Resource)) && k.Resource != "events" && !k.Resource.EndsWith("reviews"))
            .Select(k => new CompareKind(k, right[(k.Group, k.Resource)]))
            .OrderBy(k => Array.IndexOf(Preferred, k.Left.Resource) is var i and >= 0 ? i : 100).ThenBy(k => k.Title, StringComparer.Ordinal);
        foreach (var k in common) Kinds.Add(k);
        Kind = Kinds.FirstOrDefault(k => k.Title == previous) ?? Kinds.FirstOrDefault();
    }

    /// <summary>"Compare…" from a details panel: this object's kind and namespace, narrowed to its name, run straight away.</summary>
    public async Task PrefillAsync(ResourceKind kind, string? ns, string name, WorkspaceViewModel? target = null)
    {
        if (target is not null) Target = target;
        Kind = Kinds.FirstOrDefault(k => k.Left.Gvr == kind.Gvr) ?? Kind;
        if (ns is not null && LeftNamespaces.Contains(ns)) LeftNamespace = ns;
        RightNamespace = ns is not null && RightNamespaces.Contains(ns) ? ns : AllNamespaces;
        NameFilter = name;
        ShowIdentical = true;
        await CompareAsync();
    }

    partial void OnNameFilterChanged(string value) => ApplyFilter();
    partial void OnShowIdenticalChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(CompareRow? value)
    {
        DiffLines.Clear();
        if (value is null) { DiffTitle = ""; return; }
        DiffTitle = value.Entry.Status == CompareStatus.Same ? $"{value.Display}: identical" : $"{value.Display}   (− {LeftTitle}   + {RightTitle})";
        foreach (var l in value.Entry.Diff.Split('\n'))
        {
            if (l.Length == 0) continue;
            DiffLines.Add(l.StartsWith("+ ") ? new DiffLine(l, DiffLineKind.Added) : l.StartsWith("- ") ? new DiffLine(l, DiffLineKind.Removed) : l.TrimStart() == "…" ? new DiffLine("  …", DiffLineKind.Gap) : new DiffLine(l, DiffLineKind.Context));
        }
    }

    private void ApplyFilter()
    {
        var keep = _all.Where(r => (ShowIdentical || r.Entry.Status != CompareStatus.Same)
            && (NameFilter.Length == 0 || r.Display.Contains(NameFilter, StringComparison.OrdinalIgnoreCase))).ToList();
        Rows.Clear();
        foreach (var r in keep) Rows.Add(r);
        Selected = Rows.FirstOrDefault();
    }

    [RelayCommand]
    public async Task CompareAsync()
    {
        if (Kind is null) { Error = "No kind served by both clusters"; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken, Target.Session.SessionToken).Token;
        IsRunning = true; Error = null; Summary = "";
        try
        {
            var lns = LeftNamespace == AllNamespaces ? null : LeftNamespace; var rns = RightNamespace == AllNamespaces ? null : RightNamespace;
            var kind = Kind;
            var left = ListAllAsync(_ws, kind.Left, lns, ct); var right = ListAllAsync(Target, kind.Right, rns, ct);
            await Task.WhenAll(left, right);
            // Different namespaces on each side are matched by name alone; otherwise the namespace is part of the identity.
            var matchNamespace = lns == rns;
            var entries = ClusterCompare.Compare(left.Result, right.Result, matchNamespace);
            _all = entries.Select(e => new CompareRow(e)).ToList();
            var c = entries.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count());
            int N(CompareStatus s) => c.GetValueOrDefault(s);
            Summary = entries.Count == 0 ? "Nothing to compare" : $"{N(CompareStatus.Different)} differ · {N(CompareStatus.OnlyLeft)} only in {LeftTitle} · {N(CompareStatus.OnlyRight)} only in {RightTitle} · {N(CompareStatus.Same)} identical";
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsRunning = false; }
    }

    private static async Task<List<KubeObject>> ListAllAsync(WorkspaceViewModel ws, ResourceKind kind, string? ns, CancellationToken ct)
    {
        var items = new List<KubeObject>();
        string? cont = null;
        do
        {
            var page = await ws.Session.Transport.ListAsync(kind.Gvr, kind.Namespaced ? ns : null, new ListOptions { Limit = 500, Continue = cont }, ct);
            items.AddRange(page.Items.Select(i => new KubeObject(i)));
            cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
        } while (cont is not null && items.Count < 5000);
        return items;
    }

    public ValueTask DisposeAsync() { _cts?.Cancel(); return ValueTask.CompletedTask; }
}
