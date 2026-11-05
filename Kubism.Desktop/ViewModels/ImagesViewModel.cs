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

public sealed record ImageDiffRow(ImageDiffEntry Entry)
{
    public string Workload => Entry.Display;
    public string Here => string.Join(", ", Entry.Left);
    public string There => string.Join(", ", Entry.Right);
    public string Marker => Entry.Status switch { CompareStatus.Same => "=", CompareStatus.Different => "≠", CompareStatus.OnlyLeft => "◀", _ => "▶" };
    public string StatusText => Entry.Status switch
    {
        CompareStatus.Same => "identical",
        CompareStatus.Different => Entry.VersionOnly ? "version differs" : "other image",
        CompareStatus.OnlyLeft => "only here",
        _ => "only there",
    };
    public Severity Severity => Entry.Status switch { CompareStatus.Same => Severity.Ok, CompareStatus.Different => Severity.Warning, _ => Severity.Error };
}

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

    public IReadOnlyList<string> Sections { get; } = ["Images", "Repositories", "Registries", "Compare"];
    public ObservableCollection<SectionTab> Tabs { get; } = [];
    public ObservableCollection<ImageRowViewModel> Rows { get; } = [];
    public ObservableCollection<ImageGroupRow> Groups { get; } = [];
    public ObservableCollection<ImageUserRow> Users { get; } = [];
    public ObservableCollection<ImageDiffRow> DiffRows { get; } = [];
    /// <summary>The clusters this one can be compared with: every other connected cluster.</summary>
    public IEnumerable<WorkspaceViewModel> Targets => _ws.Main.Workspaces.Where(w => w != _ws);
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
    [ObservableProperty] private WorkspaceViewModel? _target;
    [ObservableProperty] private bool _showIdentical;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _compareSummary = "";

    public bool ShowImages => Section == 0;
    public bool ShowGroups => Section is 1 or 2;
    public bool ShowCompare => Section == 3;
    public bool CanCompare => Targets.Any();
    public bool NoTarget => ShowCompare && !CanCompare;
    public string TargetTitle => Target?.Title ?? "";
    public bool HasWarning => Warning.Length > 0;
    public string GroupHeader => Section == 1 ? "Repository" : "Registry";

    partial void OnSectionChanged(int value)
    {
        foreach (var t in Tabs) t.IsActive = t.Index == value;
        OnPropertyChanged(nameof(ShowImages)); OnPropertyChanged(nameof(ShowGroups)); OnPropertyChanged(nameof(ShowCompare)); OnPropertyChanged(nameof(GroupHeader));
        if (value == 3) { Target ??= Targets.FirstOrDefault(); OnPropertyChanged(nameof(CanCompare)); OnPropertyChanged(nameof(NoTarget)); _ = CompareAsync(); }
        else ShowResults();
    }
    partial void OnFilterChanged(string value) { if (Section == 3) ShowDiff(); else ShowResults(); }
    partial void OnShowIdenticalChanged(bool value) => ShowDiff();
    partial void OnTargetChanged(WorkspaceViewModel? value) { OnPropertyChanged(nameof(TargetTitle)); if (Section == 3) _ = CompareAsync(); }
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
            var snapshot = await ImageLoader.LoadAsync(_ws.Session.Transport, Namespaces(), _cts.Token);
            var rows = snapshot.Rows;
            _ws.Ui.Post(() =>
            {
                _rows = rows;
                Warning = snapshot.Warning ?? "";
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

    private IReadOnlyList<ImageDiffEntry> _diff = [];
    private CancellationTokenSource? _compareCts;

    /// <summary>Reads the other cluster (same namespaces by name) and lines up what each workload container runs.</summary>
    [RelayCommand]
    private async Task CompareAsync()
    {
        if (Target is not { } target) { DiffRows.Clear(); CompareSummary = ""; return; }
        _compareCts?.Cancel();
        _compareCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, target.Session.SessionToken);
        var ct = _compareCts.Token;
        IsComparing = true; Error = null;
        try
        {
            var scope = Namespaces();
            var here = ImageLoader.LoadAsync(_ws.Session.Transport, scope, ct);
            var there = ImageLoader.LoadAsync(target.Session.Transport, scope, ct);
            await Task.WhenAll(here, there);
            var diff = ImageComparison.Compare(here.Result.Rows, there.Result.Rows);
            _ws.Ui.Post(() =>
            {
                _diff = diff;
                int N(CompareStatus st) => diff.Count(e => e.Status == st);
                var warning = string.Join(" ", new[] { here.Result.Warning, there.Result.Warning }.Where(w => w is not null));
                CompareSummary = $"{N(CompareStatus.Different)} differ ({diff.Count(e => e.VersionOnly)} by version only) · {N(CompareStatus.OnlyLeft)} only in {_ws.Title} · {N(CompareStatus.OnlyRight)} only in {target.Title} · {N(CompareStatus.Same)} identical. Same namespaces on both sides, matched by name.{(warning.Length > 0 ? " " + warning : "")}";
                ShowDiff();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsComparing = false; }
    }

    private void ShowDiff()
    {
        DiffRows.Clear();
        var needle = Filter.Trim();
        foreach (var e in _diff.Where(e => (ShowIdentical || e.Status != CompareStatus.Same)
                     && (needle.Length == 0 || e.Display.Contains(needle, StringComparison.OrdinalIgnoreCase) || e.Left.Concat(e.Right).Any(r => r.Contains(needle, StringComparison.OrdinalIgnoreCase)))))
            DiffRows.Add(new ImageDiffRow(e));
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var csv = Section == 3 ? ImageExport.CompareCsv(DiffRows.Select(r => r.Entry), _ws.Title, Target?.Title ?? "") : ImageExport.Csv(VisibleRows(), _ws.Title);
        var path = await _ws.Dialogs.SaveFileAsync("Export CSV", Section == 3 ? "images-compare.csv" : "images.csv");
        if (path is not null) await File.WriteAllTextAsync(path, csv);
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        var path = await _ws.Dialogs.SaveFileAsync("Export Markdown", "images.md");
        if (path is not null) await File.WriteAllTextAsync(path, ImageExport.Markdown(VisibleRows(), _ws.Title));
    }

    /// <summary>What the Images tab shows now (filters applied), whichever tab is open: the export is of the images, not of the groups.</summary>
    private List<ImageRow> VisibleRows()
    {
        var needle = Filter.Trim();
        return _rows.Where(r => Passes(r) && (needle.Length == 0 || r.Reference.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.Users.Any(u => u.Display.Contains(needle, StringComparison.OrdinalIgnoreCase)))).ToList();
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

    private void OnNamespacesChanged() => _ws.Ui.Post(() => { _ = LoadAsync(); if (Section == 3) _ = CompareAsync(); });

    public ValueTask DisposeAsync() { _ws.Namespaces.Changed -= OnNamespacesChanged; _cts.Cancel(); _compareCts?.Cancel(); return ValueTask.CompletedTask; }
}
