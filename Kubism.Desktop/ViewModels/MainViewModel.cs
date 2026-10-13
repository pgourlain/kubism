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
        Search = new GlobalSearchViewModel(this);
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
    public GlobalSearchViewModel Search { get; }
    public ObservableCollection<ClusterItemViewModel> Clusters { get; } = [];
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public ObservableCollection<KubeconfigErrorRow> CatalogErrors { get; } = [];

    [ObservableProperty] private WorkspaceViewModel? _selectedWorkspace;
    [ObservableProperty] private ClusterItemViewModel? _selectedCluster;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _paletteOpen;
    [ObservableProperty] private bool _globalSearchOpen;
    [ObservableProperty] private WorkspaceViewModel? _secondaryWorkspace;
    public bool HasSecondary => SecondaryWorkspace is not null;
    partial void OnSecondaryWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsCompact = false;
        if (newValue is not null) newValue.IsCompact = true;
        OnPropertyChanged(nameof(HasSecondary));
    }

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
        var ordered = next.OrderByDescending(c => c.Metadata.Pinned).ThenBy(c => c.Metadata.Order).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
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
        var finished = false;
        var observer = new SignInObserver(line => Ui.Post(() =>
        {
            // First line from an interactive auth plugin (device code…): show the sign-in dialog.
            if (finished) return;
            signIn ??= Dialogs.ShowSignIn(item.DisplayName);
            signIn.OnLine(line);
        }));
        try
        {
            var connect = _sessions.ConnectAsync(item.Entry, observer, cts.Token);
            var session = await RunBusyAsync($"Connecting to {item.DisplayName}…", connect, cts);
            finished = true; signIn?.Close();
            if (session is null) { item.State = SessionState.Disconnected; await _sessions.DisconnectAsync(item.Id); return; }
            session.StateChanged += s => Ui.Post(() => { item.State = s; item.ErrorMessage = session.ErrorMessage; SelectedWorkspace?.RefreshHeader(); });
            item.State = session.State;
            var ws = new WorkspaceViewModel(this, session);
            Workspaces.Add(ws); SelectedWorkspace = ws;
            OnPropertyChanged(nameof(HasWorkspaces));
        }
        catch (Exception ex)
        {
            finished = true; signIn?.Close();
            item.State = SessionState.Error;
            item.ErrorMessage = ClusterSession.Describe(ex);
            AppLog.Warn($"Connect {item.DisplayName} failed", ex);
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
        if (SecondaryWorkspace == ws) SecondaryWorkspace = null;
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
    private async Task RemoveClusterAsync(ClusterItemViewModel? item)
    {
        if (item is null) return;
        if (_sessions.Get(item.Id) is not null) await DisconnectAsync(item);
        var result = _catalog.Remove(item.Entry);
        StatusText = result switch
        {
            RemoveResult.RemovedFile => $"Removed {item.Entry.FilePath} from the catalog (the file is untouched)",
            RemoveResult.RemovedFolder => $"Removed folder {Path.GetDirectoryName(item.Entry.FilePath)} from the catalog (files untouched)",
            _ => $"Hidden {item.Entry.ContextName} (File > Show hidden clusters to bring it back)",
        };
    }

    [RelayCommand]
    private void RestoreHiddenClusters() => _catalog.RestoreHidden();

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

    public event Action? SettingsApplied;

    [RelayCommand]
    public async Task EditSettingsAsync()
    {
        var editor = new SettingsEditorViewModel(Settings.Current);
        var result = await Dialogs.EditSettingsAsync(editor);
        if (result is null) return;
        result.ApplyTo(Settings.Current);
        Settings.Save();
        ApplySettings();
    }

    public void ApplySettings()
    {
        Translations.Language = Settings.Current.Language;
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = Settings.Current.Theme switch { "Light" => Avalonia.Styling.ThemeVariant.Light, "Dark" => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };
        foreach (var w in Workspaces) { w.Session.Cache.MaxInformers = Settings.Current.MaxInformersPerCluster; w.BuildNav(); }
        RebuildClusters();
        SettingsApplied?.Invoke();
        OnPropertyChanged(nameof(WindowTitle));
    }

    public async Task CheckForUpdatesAsync(bool force = false)
    {
        var current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 1);
        try
        {
            var info = await new UpdateChecker(Settings).CheckAsync(current, default, manual: force);
            if (info is { IsNewer: true }) SetStatus($"Kubism {info.Version} is available: {info.Url}");
            else if (force && info is not null) SetStatus($"Kubism is up to date (current {current.ToString(3)}, latest release {info.Version})");
        }
        catch (Exception ex) when (force) { SetStatus($"Update check failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void OpenGlobalSearch() { Search.Reset(); GlobalSearchOpen = true; }

    [RelayCommand]
    private void CloseGlobalSearch() => GlobalSearchOpen = false;

    /// <summary>Show another connected cluster next to the active one (e.g. compare deployments between staging and production).</summary>
    [RelayCommand]
    public void OpenSideBySide(WorkspaceViewModel? other)
    {
        if (other is null || other == SelectedWorkspace) return;
        SecondaryWorkspace = other;
        // Same kind as the primary pane, so the two lists can be compared directly.
        if (SelectedWorkspace?.CurrentContent is ResourceListViewModel l && other.Session.Discovery.Find(l.Kind.Gvr) is { } k) other.OpenKind(k);
    }

    [RelayCommand]
    private void CloseSplit() => SecondaryWorkspace = null;

    [RelayCommand]
    private void MoveUp(ClusterItemViewModel? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(ClusterItemViewModel? item) => Move(item, 1);

    private void Move(ClusterItemViewModel? item, int delta)
    {
        if (item is null) return;
        var list = Clusters.Where(c => c.Metadata.Pinned == item.Metadata.Pinned).ToList();
        var i = list.IndexOf(item);
        var j = Math.Clamp(i + delta, 0, list.Count - 1);
        if (i == j) return;
        list.RemoveAt(i); list.Insert(j, item);
        for (var n = 0; n < list.Count; n++) { var m = list[n].Metadata; m.Order = n; Settings.SetMetadata(list[n].Id, m); }
        RebuildClusters();
    }

    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>Runs long work with a status-bar progress indicator and a cancel button.</summary>
    public async Task<T?> RunBusyAsync<T>(string text, Task<T> work, CancellationTokenSource? cts = null)
    {
        _busyCts = cts; BusyText = text; IsBusy = true;
        try { return await work.ConfigureAwait(true); }
        finally { IsBusy = false; BusyText = ""; _busyCts = null; }
    }

    [RelayCommand]
    public Task ShowAboutAsync()
    {
        var v = typeof(MainViewModel).Assembly.GetName().Version;
        return Dialogs.ShowAboutAsync(new AboutInfo("Kubism", "Inspect. Steer. Monitor.", v is null ? "0.1.0" : $"{v.Major}.{v.Minor}.{v.Build}",
            $".NET {Environment.Version}", System.Runtime.InteropServices.RuntimeInformation.OSDescription, "MIT licensed", "https://github.com/pgourlain/kubism", DiagnosticsText(),
            System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")) ? System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt") : null,
            "https://github.com/pgourlain/kubism/blob/main/THIRD-PARTY-NOTICES.txt"));
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
        // In parallel: each workspace cancels its session first, so sockets, watches and forwards stop together.
        var all = Workspaces.ToList();
        Workspaces.Clear();
        await Task.WhenAll(all.Select(async w => { try { await w.DisposeAsync(); } catch { } }));
        await _sessions.DisposeAsync();
    }
}
