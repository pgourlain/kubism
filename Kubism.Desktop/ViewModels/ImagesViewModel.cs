using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Api;
using Kubism.Core.Images;
using Kubism.Core.Views;
using Kubism.Desktop.Navigation;

namespace Kubism.Desktop.ViewModels;

public enum ImageShow { All, Latest, NotPinned, PullFailing, MixedVersions }

/// <summary>Everything needed to put the Images page back as the user left it.</summary>
public sealed record ImagesState(int Section, string Filter, ImageShow Show, string? SelectedKey);

public sealed record ImageRowViewModel(ImageRow Row)
{
    public string Reference => Row.Reference;
    public string Registry => Row.Image.Registry;
    public string Repository => Row.Image.Repository;
    public string Version => Row.Image.Version;
    public string Digest => Row.Image.Digest is { } d ? ImageRef.ShortDigest(d) : Row.ResolvedDigests.Count switch { 0 => "", 1 => ImageRef.ShortDigest(Row.ResolvedDigests[0]), var n => $"{n} digests" };
    public string Pods => Row.Pods.ToString();
    public string Workloads => Row.Workloads.ToString();
    public string Namespaces => Row.Namespaces.Count.ToString();
    public string Policy => string.Join(", ", Row.PullPolicies);

    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            if (Row.Issues.HasFlag(ImageIssues.PullFailing)) parts.Add("Pull failing");
            if (Row.Issues.HasFlag(ImageIssues.Latest)) parts.Add("latest");
            if (Row.Issues.HasFlag(ImageIssues.MixedVersions)) parts.Add($"{Row.Versions} versions");
            return parts.Count == 0 ? "OK" : string.Join(", ", parts);
        }
    }

    public Severity Severity => Row.Issues.HasFlag(ImageIssues.PullFailing) ? Severity.Error
        : Row.Issues is ImageIssues.None or ImageIssues.NotPinned ? Severity.Ok : Severity.Warning;

    public string Key => Row.Reference;
}

/// <summary>A registry or a repository, with how many images, versions and pods sit under it.</summary>
public sealed record ImageGroupRow(string Name, int Images, string Versions, int Pods, int Namespaces, string Note);

public sealed record ImageUserRow(ImageUser User)
{
    public string Workload => User.Display;
    public string Container => User.Role == "container" ? User.Container : $"{User.Container} ({User.Role})";
    public string Pods => User.Pods.ToString();
    public string Policy => User.PullPolicy ?? "";
    public string Problem => User.PullFailures > 0 ? $"{User.PullFailures} pod(s) cannot pull" : "";
}

/// <summary>
/// The images the selected namespaces run, read from the pods: one row per reference, with who uses it, the digest the node resolved
/// and what to look at (latest or untagged, several versions of one repository, pull failures). Read-only.
/// </summary>
public sealed partial class ImagesViewModel : ViewModelBase, IClosable, INavigablePage
{
    private const int ListCap = 5000;
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<ImageRow> _rows = [];
    private string? _restoreKey;

    /// <summary>Raised with the row to bring into view after a restore.</summary>
    public event Action<object>? RevealRequested;

    public ImagesViewModel(WorkspaceViewModel ws, ImagesState? restore = null)
    {
        _ws = ws;
        ws.Namespaces.Changed += OnNamespacesChanged;
        for (var i = 0; i < Sections.Count; i++) Tabs.Add(new SectionTab(Sections[i], i) { IsActive = i == 0 });
        if (restore is not null)
        {
            _restoreKey = restore.SelectedKey;
            Filter = restore.Filter; Show = restore.Show;
            if (restore.Section >= 0 && restore.Section < Sections.Count) Section = restore.Section;
        }
        _ = LoadAsync();
    }

    public IReadOnlyList<string> Sections { get; } = ["Images", "Repositories", "Registries"];
    public ObservableCollection<SectionTab> Tabs { get; } = [];
    public ObservableCollection<ImageRowViewModel> Rows { get; } = [];
    public ObservableCollection<ImageGroupRow> Groups { get; } = [];
    public ObservableCollection<ImageUserRow> Users { get; } = [];
    [ObservableProperty] private int _section;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ImageShow _show;
    [ObservableProperty] private ImageRowViewModel? _selected;
    [ObservableProperty] private ImageGroupRow? _selectedGroup;
    [ObservableProperty] private string _digests = "";

    public bool ShowImages => Section == 0;
    public bool ShowGroups => Section != 0;
    public bool HasWarning => Warning.Length > 0;
    public string GroupHeader => Section == 1 ? "Repository" : "Registry";

    partial void OnSectionChanged(int value)
    {
        foreach (var t in Tabs) t.IsActive = t.Index == value;
        OnPropertyChanged(nameof(ShowImages)); OnPropertyChanged(nameof(ShowGroups)); OnPropertyChanged(nameof(GroupHeader));
        ShowResults();
    }
    partial void OnFilterChanged(string value) => ShowResults();
    partial void OnShowChanged(ImageShow value) { OnPropertyChanged(nameof(ShowIndex)); ShowResults(); }

    /// <summary>The "Show" combo box lists the <see cref="ImageShow"/> values in order.</summary>
    public int ShowIndex { get => (int)Show; set { if (value >= 0 && value <= (int)ImageShow.MixedVersions) Show = (ImageShow)value; } }
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedChanged(ImageRowViewModel? value)
    {
        Users.Clear();
        Digests = value is null ? "" : value.Row.ResolvedDigests.Count == 0 ? "" : "Resolved on the nodes: " + string.Join(", ", value.Row.ResolvedDigests);
        if (value is not null) foreach (var u in value.Row.Users) Users.Add(new ImageUserRow(u));
    }

    [RelayCommand]
    private void SelectSection(int index) { if (index >= 0 && index < Sections.Count) Section = index; }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>Jump to the Images tab filtered on the selected repository or registry.</summary>
    [RelayCommand]
    private void ShowImagesOfGroup()
    {
        if (SelectedGroup is null) return;
        var name = SelectedGroup.Name;
        Section = 0;
        Filter = name;
    }

    private IReadOnlyList<string?> Namespaces() => _ws.Namespaces.IsAll ? [null] : _ws.Namespaces.Selected.Cast<string?>().ToList();

    private async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var notRead = new List<string>();
            var truncated = new List<string>();
            async Task<List<KubeObject>> ReadAsync(string resource, bool required)
            {
                var kind = _ws.Session.Discovery.FindByName(resource);
                if (kind is null || !kind.CanList) { if (required) notRead.Add(resource); return []; }
                var found = new List<KubeObject>();
                try
                {
                    foreach (var ns in kind.Namespaced ? Namespaces() : [null])
                        found.AddRange(await CompareViewModel.ListAllAsync(_ws, kind, ns, _cts.Token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { lock (notRead) notRead.Add(resource); return []; }
                if (found.Count >= ListCap) lock (truncated) truncated.Add(resource);
                return found;
            }

            // Replica sets and jobs only resolve the owner of a pod: failing to read them is not an error.
            var pods = ReadAsync("pods", true);
            var sets = ReadAsync("replicasets", false);
            var jobs = ReadAsync("jobs", false);
            await Task.WhenAll(pods, sets, jobs);
            var rows = ImageInventory.Analyze(pods.Result, sets.Result, jobs.Result);
            _ws.Ui.Post(() =>
            {
                _rows = rows;
                Warning = notRead.Count == 0 && truncated.Count == 0 ? ""
                    : $"{(notRead.Count > 0 ? $"Could not read {string.Join(", ", notRead)}. " : "")}{(truncated.Count > 0 ? $"{string.Join(", ", truncated)} truncated at {ListCap}. " : "")}The list may be incomplete.";
                var repos = rows.Select(r => r.Image.Name).Distinct().Count();
                Summary = $"{rows.Count} image(s) in {repos} repositor{(repos == 1 ? "y" : "ies")} · {rows.Select(r => r.Image.Registry).Distinct().Count()} registr{(rows.Select(r => r.Image.Registry).Distinct().Count() == 1 ? "y" : "ies")} · "
                    + $"{rows.Count(r => r.Issues.HasFlag(ImageIssues.Latest))} latest or untagged · {rows.Count(r => r.Issues.HasFlag(ImageIssues.PullFailing))} failing to pull · "
                    + $"{rows.Where(r => r.Issues.HasFlag(ImageIssues.MixedVersions)).Select(r => r.Image.Name).Distinct().Count()} repositor(ies) in several versions. Selected namespaces only; read from the pods that exist now.";
                ShowResults();
                RestoreSelection();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; }
    }

    private bool Passes(ImageRow r) => Show switch
    {
        ImageShow.Latest => r.Issues.HasFlag(ImageIssues.Latest),
        ImageShow.NotPinned => r.Issues.HasFlag(ImageIssues.NotPinned),
        ImageShow.PullFailing => r.Issues.HasFlag(ImageIssues.PullFailing),
        ImageShow.MixedVersions => r.Issues.HasFlag(ImageIssues.MixedVersions),
        _ => true,
    };

    private void ShowResults()
    {
        Rows.Clear(); Groups.Clear();
        var needle = Filter.Trim();
        bool Match(ImageRow r) => needle.Length == 0
            || r.Reference.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.Users.Any(u => u.Display.Contains(needle, StringComparison.OrdinalIgnoreCase));
        var visible = _rows.Where(r => Passes(r) && Match(r)).ToList();
        if (Section == 0)
        {
            foreach (var r in visible) Rows.Add(new ImageRowViewModel(r));
            return;
        }
        var byRepo = Section == 1;
        foreach (var g in visible.GroupBy(r => byRepo ? r.Image.Name : r.Image.Registry).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var versions = g.Select(r => r.Image.Version).Distinct().Order(StringComparer.Ordinal).ToList();
            var ns = g.SelectMany(r => r.Namespaces).Distinct().Count();
            var note = g.Any(r => r.Issues.HasFlag(ImageIssues.PullFailing)) ? "Pull failing"
                : byRepo && versions.Count > 1 ? "Several versions" : g.Any(r => r.Issues.HasFlag(ImageIssues.Latest)) ? "Uses latest or no tag" : "";
            Groups.Add(new ImageGroupRow(g.Key, g.Count(), byRepo ? string.Join(", ", versions) : versions.Count.ToString(), g.Sum(r => r.Pods), ns, note));
        }
    }

    private string? SelectionKey() => Selected?.Key;

    private void RestoreSelection()
    {
        var key = _restoreKey;
        _restoreKey = null;
        if (key is null) return;
        var x = Rows.FirstOrDefault(r => r.Key == key);
        Selected = x;
        if (x is not null) RevealRequested?.Invoke(x);
    }

    public object? CaptureState() => new ImagesState(Section, Filter, Show, SelectionKey());

    [RelayCommand]
    private void OpenUser(ImageUserRow? row)
    {
        if (row?.User is { } u) _ws.OpenGraphNode(new GraphNode(u.Display, u.Kind, u.Namespace, u.Name));
    }

    private void OnNamespacesChanged() => _ws.Ui.Post(() => _ = LoadAsync());

    public ValueTask DisposeAsync() { _ws.Namespaces.Changed -= OnNamespacesChanged; _cts.Cancel(); return ValueTask.CompletedTask; }
}
