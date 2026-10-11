using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Usage;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public enum UsageMode { Config, Storage, Access, Network }

public sealed record UsageRowViewModel(UsageRow Row)
{
    public string Kind => Row.Kind;
    public string Display => Row.Display;
    public string StatusText => Row.Status switch { UsageStatus.Used => "Used", UsageStatus.Unused => "Not referenced", _ => "Managed" };
    public Severity Severity => Row.Status switch { UsageStatus.Used => Severity.Ok, UsageStatus.Unused => Severity.Warning, _ => Severity.Neutral };
    public string Count => Row.Count == 0 ? "" : Row.Count.ToString();
    public string UsedBy => Row.UsedBy;
    public string Note => Row.Note;
}

public sealed record DeadKeyRow(UsageRow Map, string Key)
{
    public string ConfigMap => Map.Display;
    public string ReadBy => Map.UsedBy;
}

public sealed record BrokenRow(BrokenReference Ref)
{
    public string Consumer => Ref.Consumer.Display;
    public string Target => Ref.Target;
    public string How => Ref.Consumer.How;
}

/// <summary>
/// Which ConfigMaps and Secrets (Config mode) or claims, volumes and storage classes (Storage mode) anything refers to, who refers to them, and
/// which references point at nothing. Read-only; Secret values are dropped as soon as the list arrives.
/// </summary>
public sealed partial class UsageViewModel : ViewModelBase, IClosable
{
    private const int ListCap = 5000;
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private ResourceUsage? _usage;

    public UsageViewModel(WorkspaceViewModel ws, UsageMode mode)
    {
        _ws = ws; Mode = mode;
        Sections = mode switch
        {
            UsageMode.Config => ["ConfigMaps", "Secrets", "Unread keys", "Broken references"],
            UsageMode.Storage => ["Claims", "Volumes", "Storage classes", "Broken references"],
            UsageMode.Access => ["Service accounts", "Broken references"],
            _ => ["Ingress classes", "Broken references"],
        };
        _ = LoadAsync();
    }

    public UsageMode Mode { get; }
    public IReadOnlyList<string> Sections { get; }
    public string Title => Mode switch { UsageMode.Config => "Config usage", UsageMode.Storage => "Storage usage", UsageMode.Access => "Service account usage", _ => "Network usage" };
    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];
    public ObservableCollection<BrokenRow> BrokenRows { get; } = [];
    public ObservableCollection<DeadKeyRow> DeadKeyRows { get; } = [];
    public ObservableCollection<Consumer> Consumers { get; } = [];
    [ObservableProperty] private int _section;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _unusedOnly;
    [ObservableProperty] private UsageRowViewModel? _selected;
    [ObservableProperty] private BrokenRow? _selectedBroken;
    [ObservableProperty] private DeadKeyRow? _selectedDeadKey;

    public bool ShowBroken => Section == Sections.Count - 1;
    public bool ShowDeadKeys => Mode == UsageMode.Config && Section == 2;
    public bool ShowRows => !ShowBroken && !ShowDeadKeys;
    public bool HasWarning => Warning.Length > 0;

    partial void OnSectionChanged(int value) { OnPropertyChanged(nameof(ShowBroken)); OnPropertyChanged(nameof(ShowDeadKeys)); OnPropertyChanged(nameof(ShowRows)); ShowResults(); }
    partial void OnFilterChanged(string value) => ShowResults();
    partial void OnUnusedOnlyChanged(bool value) => ShowResults();
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedChanged(UsageRowViewModel? value)
    {
        Consumers.Clear();
        if (value is not null) foreach (var c in value.Row.Consumers) Consumers.Add(c);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private IReadOnlyList<string?> Namespaces() => _ws.Namespaces.IsAll ? [null] : _ws.Namespaces.Selected.Cast<string?>().ToList();

    private async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            string[] workloads = ["pods", "deployments", "statefulsets", "daemonsets", "replicasets", "jobs", "cronjobs"];
            string[] targets, consumers;
            switch (Mode)
            {
                case UsageMode.Config: targets = ["configmaps", "secrets"]; consumers = [.. workloads, "ingresses", "serviceaccounts"]; break;
                case UsageMode.Storage: targets = ["persistentvolumeclaims", "persistentvolumes", "storageclasses"]; consumers = workloads; break;
                case UsageMode.Access: targets = ["serviceaccounts"]; consumers = [.. workloads, "rolebindings", "clusterrolebindings"]; break;
                default: targets = ["ingressclasses", "services"]; consumers = ["ingresses"]; break;
            }
            var listed = new HashSet<string>();
            var notRead = new List<string>();
            var truncated = new List<string>();
            var objects = new List<KubeObject>();

            async Task ReadAsync(string resource, bool isTarget)
            {
                var kind = _ws.Session.Discovery.FindByName(resource);
                if (kind is null || !kind.CanList) { if (!isTarget || kind is not null) notRead.Add(resource); return; }
                var found = new List<KubeObject>();
                try
                {
                    foreach (var ns in kind.Namespaced ? Namespaces() : [null])
                        found.AddRange(await CompareViewModel.ListAllAsync(_ws, kind, ns, _cts.Token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { notRead.Add(resource); return; }
                // Secret values are never kept. ConfigMap keys are what the unread-keys check needs; their values are not shown.
                if (kind.Kind == "Secret") foreach (var o in found) { o.Json.Remove("data"); o.Json.Remove("stringData"); o.Json.Remove("binaryData"); }
                if (found.Count >= ListCap) truncated.Add(resource);
                if (isTarget) listed.Add(kind.Kind);
                objects.AddRange(found);
            }

            await Task.WhenAll(targets.Select(t => ReadAsync(t, true)).Concat(consumers.Select(c => ReadAsync(c, false))));
            var usage = ResourceUsage.Analyze(objects, listed);
            var kinds = ModeKinds();
            _ws.Ui.Post(() =>
            {
                _usage = usage;
                Warning = notRead.Count == 0 && truncated.Count == 0 ? ""
                    : $"{(notRead.Count > 0 ? $"Could not read {string.Join(", ", notRead)}. " : "")}{(truncated.Count > 0 ? $"{string.Join(", ", truncated)} truncated at {ListCap}. " : "")}\"Not referenced\" may be wrong for what those hold.";
                var rows = usage.Rows.Where(r => kinds.Contains(r.Kind)).ToList();
                var deadKeys = rows.Sum(r => r.DeadKeys.Count);
                Summary = $"{rows.Count(r => r.Status == UsageStatus.Used)} used · {rows.Count(r => r.Status == UsageStatus.Unused)} not referenced · {rows.Count(r => r.Status == UsageStatus.External)} managed by the cluster · {usage.Broken.Count(b => BrokenKinds().Contains(b.TargetKind))} broken reference(s){(Mode == UsageMode.Config ? $" · {deadKeys} key(s) nobody reads" : "")}. Selected namespaces only.";
                ShowResults();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private string[] ModeKinds() => Mode switch
    {
        UsageMode.Config => ["ConfigMap", "Secret"],
        UsageMode.Storage => ["PersistentVolumeClaim", "PersistentVolume", "StorageClass"],
        UsageMode.Access => ["ServiceAccount"],
        _ => ["IngressClass"],
    };

    /// <summary>What a reference points at in this mode (Network also reports an ingress backend Service that does not exist).</summary>
    private string[] BrokenKinds() => Mode == UsageMode.Network ? ["IngressClass", "Service"] : Mode == UsageMode.Access ? ["ServiceAccount"] : ModeKinds();

    private string[] SectionKinds() => Mode switch
    {
        UsageMode.Config => Section switch { 0 => ["ConfigMap"], 1 => ["Secret"], _ => ["ConfigMap"] },
        UsageMode.Storage => Section switch { 0 => ["PersistentVolumeClaim"], 1 => ["PersistentVolume"], _ => ["StorageClass"] },
        _ => ModeKinds(),
    };

    private void ShowResults()
    {
        if (_usage is null) return;
        var kinds = SectionKinds();
        Rows.Clear(); BrokenRows.Clear(); DeadKeyRows.Clear();
        var needle = Filter.Trim();
        bool Match(string text) => needle.Length == 0 || text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        if (ShowBroken)
        {
            foreach (var b in _usage.Broken.Where(b => BrokenKinds().Contains(b.TargetKind) && (Match(b.Consumer.Display) || Match(b.Target)))) BrokenRows.Add(new BrokenRow(b));
            return;
        }
        if (ShowDeadKeys)
        {
            foreach (var r in _usage.Rows.Where(r => r.Kind == "ConfigMap" && r.DeadKeys.Count > 0))
                foreach (var k in r.DeadKeys.Where(k => Match(r.Display) || Match(k))) DeadKeyRows.Add(new DeadKeyRow(r, k));
            return;
        }
        foreach (var r in _usage.Rows.Where(r => kinds.Contains(r.Kind) && (!UnusedOnly || r.Status == UsageStatus.Unused) && (Match(r.Display) || Match(r.UsedBy)))) Rows.Add(new UsageRowViewModel(r));
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (ShowBroken && SelectedBroken?.Ref.Consumer is { } c) _ws.OpenGraphNode(new GraphNode(c.Display, c.Kind, c.Namespace, c.Name));
        else if (ShowDeadKeys && SelectedDeadKey?.Map is { } m) _ws.OpenGraphNode(new GraphNode(m.Display, m.Kind, m.Namespace, m.Name));
        else if (Selected?.Row is { } r) _ws.OpenGraphNode(new GraphNode(r.Display, r.Kind, r.Namespace, r.Name));
    }

    [RelayCommand]
    private void OpenConsumer(Consumer? c)
    {
        if (c is not null) _ws.OpenGraphNode(new GraphNode(c.Display, c.Kind, c.Namespace, c.Name));
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
