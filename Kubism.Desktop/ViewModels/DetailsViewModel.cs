using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Discovery;
using Kubism.Core.Views;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.ViewModels;

public sealed partial class SecretKeyViewModel(string key, JsonObject secret) : ViewModelBase
{
    public string Key { get; } = key;
    [ObservableProperty] private bool _revealed;
    [ObservableProperty] private string _display = SecretMasker.Mask;

    [RelayCommand]
    private void Toggle()
    {
        Revealed = !Revealed;
        Display = Revealed ? SecretMasker.Reveal(secret, Key) ?? "" : SecretMasker.Mask;
    }

    public string? RevealedValue => SecretMasker.Reveal(secret, Key);
}

public sealed record EventRow(string Type, string Reason, string Age, string Message, bool IsWarning);

/// <summary>Right-hand panel: Overview, YAML (with edit and conflict flow), Events, Related.</summary>
public sealed partial class DetailsViewModel : ViewModelBase
{
    private readonly WorkspaceViewModel _ws;
    private CancellationTokenSource? _cts;

    public DetailsViewModel(WorkspaceViewModel ws) { _ws = ws; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private KubeObject? _object;
    [ObservableProperty] private ResourceKind? _kind;
    [ObservableProperty] private string _yamlText = "";
    [ObservableProperty] private bool _showManagedFields;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private string? _editError;
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private string _conflictDiff = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isSecret;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Severity _statusSeverity;
    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = [];
    [ObservableProperty] private IReadOnlyList<double> _memoryHistory = [];
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private string _usageText = "";
    private IDisposable? _metricsLease;
    private Core.Metrics.MetricsService? _metricsService;
    public ObservableCollection<DetailSection> Sections { get; } = [];
    public ObservableCollection<string> Annotations { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<RelatedItem> RelatedItems { get; } = [];
    public ObservableCollection<SecretKeyViewModel> SecretKeys { get; } = [];
    public ObservableCollection<ActionItem> Actions { get; } = [];
    public string? Uid => Object?.Uid;
    private JsonObject? _conflictFresh;

    public void Show(ResourceKind kind, KubeObject obj)
    {
        Kind = kind; Object = obj; IsDeleted = false;
        Title = obj.Name;
        Subtitle = obj.Namespace is null ? kind.Kind : $"{kind.Kind} · {obj.Namespace}";
        StatusText = kind.Kind switch
        {
            "Pod" => new PodView(obj).Status, "Node" => new NodeView(obj).Status,
            "Deployment" or "StatefulSet" or "ReplicaSet" => $"{new DeploymentView(obj).Ready}/{new DeploymentView(obj).Desired} ready",
            _ => "",
        };
        StatusSeverity = ResourceRowViewModel.SeverityOf(kind.Kind, new Core.Cache.CacheItem(obj, null));
        IsOpen = true;
        SubscribeUsage(obj, kind);
        IsSecret = obj.Kind == "Secret" && obj.ApiVersion == "v1";
        if (!IsEditing) { Rebuild(); }
        _ = LoadAsync(obj);
    }

    /// <summary>Live refresh of the selected object (informer update).</summary>
    public void Update(KubeObject obj)
    {
        if (Object?.Uid != obj.Uid) return;
        Object = obj;
        if (!IsEditing) Rebuild();
    }

    public void MarkDeleted(string uid)
    {
        if (Object?.Uid == uid) { IsDeleted = true; Subtitle += " · deleted"; }
    }

    private void SubscribeUsage(KubeObject obj, ResourceKind kind)
    {
        if (_metricsService is not null) { _metricsService.Updated -= RefreshUsage; _metricsLease?.Dispose(); _metricsService = null; _metricsLease = null; }
        HasUsage = false; CpuHistory = []; MemoryHistory = [];
        if (kind.Kind is not ("Pod" or "Node") || _ws.Session.Metrics is not { } m) return;
        _metricsService = m; _metricsLease = m.Acquire(); m.Updated += RefreshUsage;
        RefreshUsage();
    }

    private void RefreshUsage()
    {
        if (Object is null || _metricsService is null) return;
        var key = Object.Kind == "Pod" ? $"pod/{Object.Namespace}/{Object.Name}" : $"node/{Object.Name}";
        var h = _metricsService.History(key);
        _ws.Ui.Post(() =>
        {
            HasUsage = h.Count > 0;
            CpuHistory = h.Select(x => x.Cpu).ToList(); MemoryHistory = h.Select(x => x.Memory).ToList();
            UsageText = h.Count == 0 ? "" : $"{Core.Views.Quantities.FormatCpu(h[^1].Cpu)} · {Core.Views.Quantities.FormatBytes(h[^1].Memory)} (last {h.Count} samples)";
        });
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false; IsEditing = false; _cts?.Cancel(); Object = null;
        if (_metricsService is not null) { _metricsService.Updated -= RefreshUsage; _metricsLease?.Dispose(); _metricsService = null; _metricsLease = null; }
    }

    partial void OnShowManagedFieldsChanged(bool value) { if (!IsEditing) BuildYaml(); }

    private void Rebuild()
    {
        if (Object is null) return;
        Sections.Clear();
        foreach (var s in DetailSectionBuilder.Build(Object, _ws.Session.ClockSkew)) Sections.Add(s);
        Annotations.Clear();
        foreach (var a in DetailSectionBuilder.Annotations(Object)) Annotations.Add(a);
        SecretKeys.Clear();
        if (IsSecret) foreach (var k in SecretMasker.Keys(Object.Json)) SecretKeys.Add(new SecretKeyViewModel(k, Object.Json));
        BuildYaml();
        BuildActions();
    }

    private void BuildYaml()
    {
        if (Object is null) return;
        // Secrets are masked even in the YAML tab; managedFields hidden by default.
        var json = SecretMasker.MaskedCopy(Object.Json);
        if (!ShowManagedFields) json = SecretMasker.WithoutManagedFields(json);
        YamlText = YamlJson.ToYaml(json);
    }

    private async void BuildActions()
    {
        if (Object is null || Kind is null) return;
        var items = _ws.ActionsFor(Kind, Object);
        Actions.Clear();
        foreach (var a in items) Actions.Add(a);
        await _ws.ApplyRbacAsync(Kind, Object, items);
    }

    // ---------- events & related ----------

    private async Task LoadAsync(KubeObject obj)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _ws.Session.SessionToken).Token;
        try
        {
            var list = await _ws.Session.Transport.ListAsync(Gvr.Events, null, new ListOptions { FieldSelector = $"involvedObject.uid={obj.Uid}" }, ct).ConfigureAwait(false);
            var events = list.Items.Select(i => new EventView(new KubeObject(i))).OrderByDescending(e => e.LastSeen).ToList();
            var skew = _ws.Session.ClockSkew;
            var rows = events.Select(e => new EventRow(e.Type, e.Reason, KubeTime.Age(e.LastSeen, skew), e.Message, e.IsWarning)).ToList();
            var related = await LoadRelatedAsync(obj, ct).ConfigureAwait(false);
            _ws.Ui.Post(() =>
            {
                if (Object?.Uid != obj.Uid) return;
                Events.Clear(); foreach (var r in rows) Events.Add(r);
                RelatedItems.Clear(); foreach (var r in related) RelatedItems.Add(r);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Details load failed: {ex.Message}"); }
    }

    private async Task<IReadOnlyList<RelatedItem>> LoadRelatedAsync(KubeObject obj, CancellationToken ct)
    {
        var t = _ws.Session.Transport;
        var disc = _ws.Session.Discovery;
        var ns = obj.Namespace;
        var candidates = new List<KubeObject>();
        async Task Load(Gvr gvr)
        {
            try
            {
                var l = await t.ListAsync(gvr, ns, new ListOptions(), ct).ConfigureAwait(false);
                candidates.AddRange(l.Items.Select(i => new KubeObject(i)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (ns is not null)
        {
            foreach (var g in new[] { Gvr.Pods, Gvr.ReplicaSets, Gvr.Services, Gvr.Jobs }) await Load(g).ConfigureAwait(false);
            await Load(Gvr.Deployments).ConfigureAwait(false);
        }
        KubeObject? Lookup(string kind, string? n, string name) => candidates.FirstOrDefault(c => c.Kind == kind && c.Name == name);
        // Owners that aren't in the preloaded kinds (e.g. CronJob, custom controllers): fetch them directly.
        var rel = Related.For(obj, (k, n, name) =>
        {
            var found = Lookup(k, n, name);
            if (found is not null) return found;
            var kd = disc.Kinds.FirstOrDefault(x => x.Kind == k);
            if (kd is null) return null;
            try { return t.GetAsync(kd.Gvr.ObjectPath(n, name), ct).GetAwaiter().GetResult() is JsonObject j ? new KubeObject(j) : null; }
            catch { return null; }
        }, candidates);
        return rel;
    }

    // ---------- editing ----------

    [RelayCommand]
    private void BeginEdit()
    {
        if (Object is null) return;
        if (IsSecret) { EditError = "Secrets can't be edited here: values are never shown. Use Create from YAML."; return; }
        var json = SecretMasker.WithoutManagedFields(Object.Json);
        EditText = YamlJson.ToYaml(json);
        EditError = null; HasConflict = false; IsEditing = true; SelectedTab = 1;
    }

    [RelayCommand]
    private void CancelEdit() { IsEditing = false; EditError = null; HasConflict = false; Rebuild(); }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Object is null) return;
        EditError = null;
        try
        {
            await _ws.Session.Actions.ReplaceAsync(EditText, Object.Namespace, _ws.Session.SessionToken);
            IsEditing = false; HasConflict = false;
            _ws.Main.SetStatus($"Saved {Object.Kind}/{Object.Name}");
        }
        catch (ConflictException c)
        {
            _conflictFresh = c.Fresh;
            ConflictDiff = Diff(EditText, YamlJson.ToYaml(SecretMasker.WithoutManagedFields(c.Fresh)));
            HasConflict = true;
            EditError = "The object was modified on the server: " + c.Message;
        }
        catch (Exception ex)
        {
            // Validation and admission errors come from the API server and are shown verbatim.
            EditError = ex.Message;
        }
    }

    [RelayCommand]
    private void ReloadAndReapply()
    {
        // Keep the user's text but rebase onto the fresh resourceVersion.
        if (_conflictFresh is null) return;
        try
        {
            var mine = YamlJson.Parse(EditText) as JsonObject ?? new JsonObject();
            var rv = _conflictFresh["metadata"]?["resourceVersion"]?.DeepClone();
            (mine["metadata"] as JsonObject ?? (JsonObject)(mine["metadata"] = new JsonObject()))["resourceVersion"] = rv;
            EditText = YamlJson.ToYaml(mine);
            HasConflict = false; EditError = null;
        }
        catch (Exception ex) { EditError = ex.Message; }
    }

    [RelayCommand]
    private void DiscardMine()
    {
        if (_conflictFresh is null) return;
        EditText = YamlJson.ToYaml(SecretMasker.WithoutManagedFields(_conflictFresh));
        HasConflict = false; EditError = null;
    }

    /// <summary>Simple line diff: "- " lines only in theirs? no: "-" are in my text, "+" are on the server.</summary>
    public static string Diff(string mine, string server)
    {
        var a = mine.Replace("\r", "").Split('\n'); var b = server.Replace("\r", "").Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var sb = new System.Text.StringBuilder();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { sb.Append("  ").AppendLine(a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { sb.Append("- ").AppendLine(a[x]); x++; }
            else { sb.Append("+ ").AppendLine(b[y]); y++; }
        }
        for (; x < a.Length; x++) sb.Append("- ").AppendLine(a[x]);
        for (; y < b.Length; y++) sb.Append("+ ").AppendLine(b[y]);
        return sb.ToString();
    }

    [RelayCommand]
    private async Task CopyYamlAsync() => await _ws.Clipboard.SetTextAsync(YamlText);

    [RelayCommand]
    private async Task DownloadYamlAsync()
    {
        var path = await _ws.Dialogs.SaveFileAsync("Save YAML", $"{Object?.Name ?? "object"}.yaml");
        if (path is not null) await File.WriteAllTextAsync(path, YamlText);
    }

    [RelayCommand]
    private async Task CopySecretValueAsync(SecretKeyViewModel key)
    {
        // Explicit user action on one key.
        if (key.RevealedValue is { } v) await _ws.Clipboard.SetTextAsync(v);
    }
}
