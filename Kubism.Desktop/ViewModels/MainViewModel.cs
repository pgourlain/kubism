using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.ViewModels;

public sealed record KubeconfigErrorRow(string Text);

public sealed class SignInObserver(Action<string> onLine) : IExecPluginObserver
{
    public void OnPluginOutput(string line) => onLine(line);
}

/// <summary>Root view model: cluster catalog, connected workspaces, busy state, command palette.</summary>
public sealed partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IKubeconfigCatalog _catalog;
    private readonly IClusterSessionManager _sessions;
    private CancellationTokenSource? _busyCts;

    public MainViewModel(IKubeconfigCatalog catalog, IClusterSessionManager sessions, ISettingsStore settings, IDialogService dialogs,
        IClipboardService clipboard, IBrowserLauncher browser, IUiThread ui, IUsageStore usage, ITokenProvider? azure = null)
    {
        _catalog = catalog; _sessions = sessions; Settings = settings; Dialogs = dialogs; Clipboard = clipboard; Browser = browser; Ui = ui;
        UsageStore = usage; AzureTokens = azure;
        Translations.Language = settings.Current.Language;
        Palette = new CommandPaletteViewModel(this);
        RebuildClusters();
        _catalog.CatalogChanged += () => Ui.Post(RebuildClusters);
    }

    public ISettingsStore Settings { get; }
    public IDialogService Dialogs { get; }
    public IClipboardService Clipboard { get; }
    public IBrowserLauncher Browser { get; }
    public IUiThread Ui { get; }
    public IUsageStore UsageStore { get; }
    public ITokenProvider? AzureTokens { get; }
    public IClusterSessionManager Sessions => _sessions;
    public CommandPaletteViewModel Palette { get; }
    public ObservableCollection<ClusterItemViewModel> Clusters { get; } = [];
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public ObservableCollection<KubeconfigErrorRow> CatalogErrors { get; } = [];

    [ObservableProperty] private WorkspaceViewModel? _selectedWorkspace;
    [ObservableProperty] private ClusterItemViewModel? _selectedCluster;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _paletteOpen;

    public bool HasWorkspaces => Workspaces.Count > 0;
    public bool ShowWelcome => SelectedWorkspace is null;
    public string WindowTitle => SelectedWorkspace is { } w ? $"{w.Title}{(w.HasBanner ? $" [{w.SafetyMode}]" : "")} · Kubism" : "Kubism";

    partial void OnSelectedWorkspaceChanged(WorkspaceViewModel? value)
    {
        OnPropertyChanged(nameof(ShowWelcome)); OnPropertyChanged(nameof(WindowTitle));
    }

    public void SetStatus(string text) => Ui.Post(() => StatusText = text);

    public void RebuildClusters()
    {
        var existing = Clusters.ToDictionary(c => c.Id);
        var next = new List<ClusterItemViewModel>();
        foreach (var e in _catalog.Entries)
        {
            if (existing.TryGetValue(e.Id, out var vm)) next.Add(vm);
            else
            {
                vm = new ClusterItemViewModel(e, Settings);
                var s = _sessions.Get(e.Id);
                if (s is not null) vm.State = s.State;
                next.Add(vm);
            }
        }
        var ordered = next.OrderByDescending(c => c.Metadata.Pinned).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (!Clusters.SequenceEqual(ordered)) { Clusters.Clear(); foreach (var c in ordered) Clusters.Add(c); }
        CatalogErrors.Clear();
        foreach (var err in _catalog.Errors) CatalogErrors.Add(new KubeconfigErrorRow($"{err.FilePath}: {err.Message}"));
        OnPropertyChanged(nameof(HasCatalogErrors));
    }

    public bool HasCatalogErrors => CatalogErrors.Count > 0;

    // ---------- connect / disconnect ----------

    [RelayCommand]
    public async Task ConnectAsync(ClusterItemViewModel? item)
    {
        if (item is null) return;
        var existing = Workspaces.FirstOrDefault(w => w.Id == item.Id);
        if (existing is not null) { SelectedWorkspace = existing; return; }
        item.State = SessionState.Connecting; item.ErrorMessage = null;
        using var cts = new CancellationTokenSource();
        SignInHandle? signIn = null;
        var observer = new SignInObserver(line => Ui.Post(() =>
        {
            // First line from an interactive auth plugin (device code…): show the sign-in dialog.
            signIn ??= Dialogs.ShowSignIn(item.DisplayName);
            signIn.OnLine(line);
        }));
        try
        {
            var connect = _sessions.ConnectAsync(item.Entry, observer, cts.Token);
            var session = await RunBusyAsync($"Connecting to {item.DisplayName}…", connect, cts);
            signIn?.Close();
            if (session is null) { item.State = SessionState.Disconnected; await _sessions.DisconnectAsync(item.Id); return; }
            session.StateChanged += s => Ui.Post(() => { item.State = s; item.ErrorMessage = session.ErrorMessage; SelectedWorkspace?.RefreshHeader(); });
            item.State = session.State;
            var ws = new WorkspaceViewModel(this, session);
            Workspaces.Add(ws); SelectedWorkspace = ws;
            OnPropertyChanged(nameof(HasWorkspaces));
        }
        catch (Exception ex)
        {
            signIn?.Close();
            item.State = SessionState.Error;
            item.ErrorMessage = ClusterSession.Describe(ex);
            AppLog.Warn($"Connect {item.DisplayName} failed: {ex.Message}");
            await _sessions.DisconnectAsync(item.Id);
        }
    }

    [RelayCommand]
    public async Task DisconnectAsync(ClusterItemViewModel? item)
    {
        if (item is null) return;
        var ws = Workspaces.FirstOrDefault(w => w.Id == item.Id);
        if (ws is not null) await CloseWorkspaceAsync(ws);
        else { await _sessions.DisconnectAsync(item.Id); item.State = SessionState.Disconnected; item.ErrorMessage = null; }
    }

    [RelayCommand]
    public async Task CloseWorkspaceAsync(WorkspaceViewModel? ws)
    {
        if (ws is null) return;
        var idx = Workspaces.IndexOf(ws);
        Workspaces.Remove(ws);
        if (SelectedWorkspace == ws) SelectedWorkspace = Workspaces.Count > 0 ? Workspaces[Math.Min(idx, Workspaces.Count - 1)] : null;
        await ws.DisposeAsync();
        await _sessions.DisconnectAsync(ws.Id);
        if (Clusters.FirstOrDefault(c => c.Id == ws.Id) is { } item) { item.State = SessionState.Disconnected; item.ErrorMessage = null; }
        OnPropertyChanged(nameof(HasWorkspaces));
    }

    public void SelectWorkspaceByIndex(int i)
    {
        if (i >= 0 && i < Workspaces.Count) SelectedWorkspace = Workspaces[i];
    }

    // ---------- catalog ----------

    [RelayCommand]
    private async Task AddKubeconfigAsync()
    {
        var path = await Dialogs.PickFileAsync("Add kubeconfig file");
        if (path is not null) _catalog.AddPath(path);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = await Dialogs.PickFolderAsync("Add kubeconfig folder");
        if (path is not null) _catalog.AddPath(path);
    }

    [RelayCommand]
    private void ReloadCatalog() => _catalog.Reload();

    [RelayCommand]
    private async Task EditClusterAsync(ClusterItemViewModel? item)
    {
        if (item is null) return;
        var editor = new ClusterEditorViewModel(item.Entry.ContextName, item.Metadata);
        var result = await Dialogs.EditClusterAsync(editor);
        if (result is null) return;
        Settings.SetMetadata(item.Id, result.ToMetadata(item.Metadata));
        item.RefreshMetadata();
        Workspaces.FirstOrDefault(w => w.Id == item.Id)?.RefreshHeader();
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private void TogglePin(ClusterItemViewModel? item)
    {
        if (item is null) return;
        var m = item.Metadata; m.Pinned = !m.Pinned;
        Settings.SetMetadata(item.Id, m);
        RebuildClusters();
    }

    // ---------- misc ----------

    [RelayCommand]
    private void OpenPalette() { Palette.Reset(); PaletteOpen = true; }

    [RelayCommand]
    private void ClosePalette() => PaletteOpen = false;

    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>Runs long work with a status-bar progress indicator and a cancel button.</summary>
    public async Task<T?> RunBusyAsync<T>(string text, Task<T> work, CancellationTokenSource? cts = null)
    {
        _busyCts = cts; BusyText = text; IsBusy = true;
        try { return await work.ConfigureAwait(true); }
        finally { IsBusy = false; BusyText = ""; _busyCts = null; }
    }

    public string DiagnosticsText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Kubism {typeof(MainViewModel).Assembly.GetName().Version}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"Connected clusters: {Workspaces.Count}");
        foreach (var w in Workspaces)
            sb.AppendLine($"  server {w.ServerVersion}, state {w.Session.State}, informers {w.Session.Cache.Informers.Count}, objects {w.Session.Cache.Informers.Sum(i => i.Count)}");
        sb.AppendLine($"Local usage store: {UsageStore.SizeBytes / 1024} KiB");
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in Workspaces.ToList()) await w.DisposeAsync();
        Workspaces.Clear();
        await _sessions.DisposeAsync();
    }
}
