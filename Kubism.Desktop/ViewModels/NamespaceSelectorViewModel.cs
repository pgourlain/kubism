using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Sessions;

namespace Kubism.Desktop.ViewModels;

public sealed partial class NamespaceChoice(string name, bool selected) : ViewModelBase
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected = selected;
}

/// <summary>Namespace multi-select shared by every list of a workspace; selection is remembered per cluster.</summary>
public sealed partial class NamespaceSelectorViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ClusterSession _session;
    private readonly Services.IUiThread _ui;
    private InformerLease? _lease;
    private IDisposable? _sub;
    private bool _suppress;

    private readonly Core.Catalog.ISettingsStore _settingsStore;

    public NamespaceSelectorViewModel(ClusterSession session, Core.Catalog.ISettingsStore settings, Services.IUiThread ui)
    {
        _session = session; _ui = ui; _settingsStore = settings;
        var saved = session.Metadata.Namespaces; // manual namespaces
        foreach (var n in saved) Add(n);
        var sel = _sessionSettings().TryGetValue(session.Entry.Id, out var s) ? s : [];
        _selectedNames = [.. sel];
        foreach (var n in _selectedNames) Add(n);
        var def = session.Entry.DefaultNamespace ?? session.Metadata.DefaultNamespace;
        if (_selectedNames.Count == 0 && def is not null) { _selectedNames = [def]; Add(def); }
        RebuildSelection();
        Start();
    }

    private Dictionary<string, List<string>> _sessionSettings() => _settingsStore.Current.SelectedNamespaces;

    private List<string> _selectedNames;
    public RangeObservableCollection<NamespaceChoice> Choices { get; } = [];
    [ObservableProperty] private bool _isForbidden;
    [ObservableProperty] private string _manualText = "";

    /// <summary>Selected namespaces; empty means "all namespaces".</summary>
    public IReadOnlyList<string> Selected => _selectedNames;
    public bool IsAll => _selectedNames.Count == 0;
    public string Summary => IsAll ? "All namespaces" : _selectedNames.Count == 1 ? _selectedNames[0] : $"{_selectedNames.Count} namespaces";
    public event Action? Changed;

    private void Start()
    {
        _lease = _session.Cache.Subscribe(Gvr.Namespaces);
        _sub = Kubism.Core.Events.ObservableExtensions.Subscribe(_lease.Informer.Changes, _ => _ui.Post(Sync));
        _lease.Informer.StateChanged += st => _ui.Post(() => { IsForbidden = st == InformerState.Failed; });
        _ui.Post(Sync);
    }

    private void Add(string name)
    {
        if (Choices.Any(c => c.Name == name)) return;
        var c = new NamespaceChoice(name, _selectedNames.Contains(name));
        c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NamespaceChoice.IsSelected) && !_suppress) OnChoiceToggled(); };
        var idx = 0;
        while (idx < Choices.Count && string.CompareOrdinal(Choices[idx].Name, name) < 0) idx++;
        Choices.Insert(idx, c);
    }

    private void Sync()
    {
        if (_lease is null) return;
        _suppress = true;
        foreach (var n in _lease.Informer.Snapshot().Select(i => i.Object.Name)) Add(n);
        _suppress = false;
        IsForbidden = _lease.Informer.State == InformerState.Failed;
    }

    private void OnChoiceToggled()
    {
        _selectedNames = Choices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        RebuildSelection();
        Persist();
        Changed?.Invoke();
    }

    private void RebuildSelection()
    {
        OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(IsAll)); OnPropertyChanged(nameof(Summary));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void SelectAll()
    {
        _suppress = true;
        foreach (var c in Choices) c.IsSelected = false;
        _suppress = false;
        OnChoiceToggled();
    }

    public void Select(string name)
    {
        _suppress = true;
        Add(name);
        foreach (var c in Choices) c.IsSelected = c.Name == name;
        _suppress = false;
        OnChoiceToggled();
    }

    /// <summary>Restores an earlier selection exactly (several namespaces, or none = all).</summary>
    public void SetSelection(IReadOnlyList<string> names)
    {
        _suppress = true;
        foreach (var n in names) Add(n);
        foreach (var c in Choices) c.IsSelected = names.Contains(c.Name);
        _suppress = false;
        OnChoiceToggled();
    }

    /// <summary>When listing namespaces is forbidden the user types namespaces; they're stored per cluster.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddManual() => AddManual(ManualText);

    public void AddManual(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        if (!_session.Metadata.Namespaces.Contains(name)) { _session.Metadata.Namespaces.Add(name); _settingsStore.SetMetadata(_session.Entry.Id, _session.Metadata); }
        _suppress = true; Add(name); _suppress = false;
        Choices.First(c => c.Name == name).IsSelected = true;
        ManualText = "";
    }

    private void Persist()
    {
        _sessionSettings()[_session.Entry.Id] = [.. _selectedNames];
        _settingsStore.Save();
    }

    public async ValueTask DisposeAsync()
    {
        _sub?.Dispose();
        _lease?.Dispose();
        await Task.CompletedTask;
    }
}
