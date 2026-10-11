using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Navigation;

/// <param name="KindKey">"group|resource" of a kind; "helm" for a Helm release.</param>
public sealed record ObjectRef(string KindKey, string? Namespace, string Name);

/// <summary>Where the user is in one cluster: a page, the object opened on it, the namespaces shown.</summary>
/// <param name="Title">Short name for the history menu ("Secrets", "Config usage").</param>
/// <param name="Crumbs">"Config › Secrets › web-config".</param>
public sealed record NavLocation(NavTarget Target, string? KindKey, IReadOnlyList<string> Namespaces, ObjectRef? Object, string Title, string Crumbs)
{
    /// <summary>The same page and object. The namespaces are not part of it: changing them is not navigation.</summary>
    public bool SamePlace(NavLocation other) => Target == other.Target && KindKey == other.KindKey && Object == other.Object;
}

/// <summary>An entry: a location and what the page showed when the user left it (filter, tab, selection…).</summary>
public sealed class NavEntry(NavLocation location)
{
    public NavLocation Location { get; set; } = location;
    public object? State { get; set; }
}

/// <param name="State">What the page being left reports, or null.</param>
public sealed record LeaveSnapshot(object? State, IReadOnlyList<string> Namespaces);

/// <summary>
/// The back/forward history of one cluster, like a browser's: one list and a position. Recording a place drops what was ahead. What a page
/// showed is captured when it is left (through <c>capture</c>), never on every click, so changing a filter is not an entry.
/// </summary>
public sealed class NavigationHistory(Func<LeaveSnapshot> capture)
{
    public const int Max = 50;
    private readonly List<NavEntry> _entries = [];
    private int _index = -1;
    private bool _captured;

    public event Action? Changed;

    public IReadOnlyList<NavEntry> Entries => _entries;
    public NavEntry? Current => _index >= 0 ? _entries[_index] : null;
    public int Index => _index;
    public bool CanBack => _index > 0;
    public bool CanForward => _index >= 0 && _index < _entries.Count - 1;
    public NavEntry? BackEntry => CanBack ? _entries[_index - 1] : null;
    public NavEntry? ForwardEntry => CanForward ? _entries[_index + 1] : null;

    /// <summary>
    /// Captures the page being left now. A caller that is about to change something the capture reads (the namespaces) calls this first;
    /// the capture is then not repeated by <see cref="Record"/> or a move. <see cref="EndNavigation"/> lets go of it.
    /// </summary>
    public void BeginNavigation() { Capture(); _captured = true; }
    public void EndNavigation() => _captured = false;

    private void Capture()
    {
        if (_captured || Current is not { } cur) return;
        var snap = capture();
        cur.State = snap.State;
        cur.Location = cur.Location with { Namespaces = snap.Namespaces };
    }

    /// <summary>Makes <paramref name="next"/> the current place. False (nothing recorded) when it is the one we are already at.</summary>
    public bool Record(NavLocation next)
    {
        if (Current is { } cur && cur.Location.SamePlace(next)) { cur.Location = cur.Location with { Namespaces = next.Namespaces, Title = next.Title, Crumbs = next.Crumbs }; return false; }
        Capture();
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new NavEntry(next));
        if (_entries.Count > Max) _entries.RemoveAt(0);
        _index = _entries.Count - 1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves to an entry and returns it; the caller shows it. Null when there is nowhere to go.</summary>
    public NavEntry? GoTo(int index)
    {
        if (index < 0 || index >= _entries.Count || index == _index) return null;
        Capture();
        _index = index;
        Changed?.Invoke();
        return _entries[_index];
    }

    public NavEntry? Back() => GoTo(_index - 1);
    public NavEntry? Forward() => GoTo(_index + 1);

    public void Clear() { _entries.Clear(); _index = -1; Changed?.Invoke(); }
}
