using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kubism.Desktop.ViewModels;

public sealed record PaletteItem(string Title, string Subtitle, string Category, Func<Task> Execute);

/// <summary>Ctrl/⌘+K: jump to any kind (name or short name), namespace or cluster.</summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private List<PaletteItem> _all = [];

    public CommandPaletteViewModel(MainViewModel main) { _main = main; }

    public ObservableCollection<PaletteItem> Results { get; } = [];
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;

    public void Reset()
    {
        _all = Build();
        Query = "";
        Filter();
    }

    private List<PaletteItem> Build()
    {
        var items = new List<PaletteItem>();
        var ws = _main.SelectedWorkspace;
        if (ws is not null)
        {
            if (ws.CanGoBack) items.Add(new PaletteItem("Go back", ws.BackTip, "Navigation", () => { ws.GoBack(); return Task.CompletedTask; }));
            if (ws.CanGoForward) items.Add(new PaletteItem("Go forward", ws.ForwardTip, "Navigation", () => { ws.GoForward(); return Task.CompletedTask; }));
            foreach (var k in ws.Session.Discovery.Kinds.Where(k => k.CanList).OrderBy(k => k.Kind))
            {
                var kind = k;
                var shorts = k.ShortNames.Count > 0 ? $" ({string.Join(", ", k.ShortNames)})" : "";
                items.Add(new PaletteItem($"{k.Kind}{shorts}", k.Group.Length == 0 ? k.Resource : $"{k.Resource}.{k.Group}", "Kind", () => { ws.OpenKind(kind); return Task.CompletedTask; }));
            }
            foreach (var n in ws.Namespaces.Choices)
            {
                var name = n.Name;
                items.Add(new PaletteItem(name, "namespace", "Namespace", () => { ws.Namespaces.Select(name); return Task.CompletedTask; }));
            }
            items.Add(new PaletteItem("All namespaces", "namespace", "Namespace", () => { ws.Namespaces.SelectAll(); return Task.CompletedTask; }));
        }
        foreach (var c in _main.Clusters)
        {
            var cluster = c;
            items.Add(new PaletteItem(c.DisplayName, c.Host, "Cluster", () => _main.ConnectAsync(cluster)));
        }
        return items;
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var q = Query.Trim();
        IEnumerable<PaletteItem> r = _all;
        if (q.Length > 0)
            r = _all.Select(i => (item: i, score: Score(i, q))).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.item);
        Results.Clear();
        foreach (var i in r.Take(40)) Results.Add(i);
        Selected = Results.FirstOrDefault();
    }

    private static int Score(PaletteItem i, string q)
    {
        var t = i.Title; var s = i.Subtitle;
        // Short names: "po" must find Pod before anything that merely contains it.
        // The plural resource name typed in full ("pods", "deployments") must find that kind first.
        if (s.Equals(q, StringComparison.OrdinalIgnoreCase) || s.StartsWith(q + ".", StringComparison.OrdinalIgnoreCase)) return 110;
        if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100 - Math.Min(t.Length, 50) / 2;
        var inParens = t.IndexOf('(');
        if (inParens > 0 && t[(inParens + 1)..].Split([',', ' ', ')'], StringSplitOptions.RemoveEmptyEntries).Any(x => x.Equals(q, StringComparison.OrdinalIgnoreCase))) return 95;
        if (t.Contains(q, StringComparison.OrdinalIgnoreCase)) return 60;
        if (s.Contains(q, StringComparison.OrdinalIgnoreCase)) return 30;
        return 0;
    }

    [RelayCommand]
    public async Task ExecuteAsync(PaletteItem? item = null)
    {
        item ??= Selected;
        if (item is null) return;
        _main.PaletteOpen = false;
        await item.Execute();
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? 0 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }
}
