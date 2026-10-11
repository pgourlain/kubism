using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Usage;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public enum UsageMode { Config, Storage }

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
        Sections = mode == UsageMode.Config ? ["ConfigMaps", "Secrets", "Broken references"] : ["Claims", "Volumes", "Storage classes", "Broken references"];
        _ = LoadAsync();
    }

    public UsageMode Mode { get; }
    public IReadOnlyList<string> Sections { get; }
    public string Title => Mode == UsageMode.Config ? "Config usage" : "Storage usage";
    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];
    public ObservableCollection<BrokenRow> BrokenRows { get; } = [];
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

    public bool ShowBroken => Section == Sections.Count - 1;
    public bool ShowRows => !ShowBroken;
    public bool HasWarning => Warning.Length > 0;

    partial void OnSectionChanged(int value) { OnPropertyChanged(nameof(ShowBroken)); OnPropertyChanged(nameof(ShowRows)); ShowResults(); }
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
            var targets = Mode == UsageMode.Config ? new[] { "configmaps", "secrets" } : ["persistentvolumeclaims", "persistentvolumes", "storageclasses"];
            var consumers = Mode == UsageMode.Config
                ? new[] { "pods", "deployments", "statefulsets", "daemonsets", "replicasets", "jobs", "cronjobs", "ingresses", "serviceaccounts" }
                : ["pods", "deployments", "statefulsets", "daemonsets", "replicasets", "jobs", "cronjobs"];
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
                foreach (var o in found) { o.Json.Remove("data"); o.Json.Remove("stringData"); o.Json.Remove("binaryData"); }
                if (found.Count >= ListCap) truncated.Add(resource);
                if (isTarget) listed.Add(kind.Kind);
                objects.AddRange(found);
            }

            await Task.WhenAll(targets.Select(t => ReadAsync(t, true)).Concat(consumers.Select(c => ReadAsync(c, false))));
            var usage = ResourceUsage.Analyze(objects, listed);
            var kinds = Mode == UsageMode.Config ? new[] { "ConfigMap", "Secret" } : ["PersistentVolumeClaim", "PersistentVolume", "StorageClass"];
            _ws.Ui.Post(() =>
            {
                _usage = usage;
                Warning = notRead.Count == 0 && truncated.Count == 0 ? ""
                    : $"{(notRead.Count > 0 ? $"Could not read {string.Join(", ", notRead)}. " : "")}{(truncated.Count > 0 ? $"{string.Join(", ", truncated)} truncated at {ListCap}. " : "")}\"Not referenced\" may be wrong for what those hold.";
                var rows = usage.Rows.Where(r => kinds.Contains(r.Kind)).ToList();
                Summary = $"{rows.Count(r => r.Status == UsageStatus.Used)} used · {rows.Count(r => r.Status == UsageStatus.Unused)} not referenced · {rows.Count(r => r.Status == UsageStatus.External)} managed by the cluster · {usage.Broken.Count(b => kinds.Contains(b.TargetKind))} broken reference(s). Selected namespaces only.";
                ShowResults();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private string[] SectionKinds() => Mode == UsageMode.Config
        ? Section switch { 0 => ["ConfigMap"], 1 => ["Secret"], _ => ["ConfigMap", "Secret"] }
        : Section switch { 0 => ["PersistentVolumeClaim"], 1 => ["PersistentVolume"], 2 => ["StorageClass"], _ => ["PersistentVolumeClaim", "PersistentVolume", "StorageClass"] };

    private void ShowResults()
    {
        if (_usage is null) return;
        var kinds = SectionKinds();
        Rows.Clear(); BrokenRows.Clear();
        var needle = Filter.Trim();
        bool Match(string text) => needle.Length == 0 || text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        if (ShowBroken)
        {
            foreach (var b in _usage.Broken.Where(b => kinds.Contains(b.TargetKind) && (Match(b.Consumer.Display) || Match(b.Target)))) BrokenRows.Add(new BrokenRow(b));
            return;
        }
        foreach (var r in _usage.Rows.Where(r => kinds.Contains(r.Kind) && (!UnusedOnly || r.Status == UsageStatus.Unused) && (Match(r.Display) || Match(r.UsedBy)))) Rows.Add(new UsageRowViewModel(r));
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (ShowBroken && SelectedBroken?.Ref.Consumer is { } c) _ws.OpenGraphNode(new GraphNode(c.Display, c.Kind, c.Namespace, c.Name));
        else if (Selected?.Row is { } r) _ws.OpenGraphNode(new GraphNode(r.Display, r.Kind, r.Namespace, r.Name));
    }

    [RelayCommand]
    private void OpenConsumer(Consumer? c)
    {
        if (c is not null) _ws.OpenGraphNode(new GraphNode(c.Display, c.Kind, c.Namespace, c.Name));
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
