using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core.Catalog;
using Kubism.Core.Exec;
using Kubism.Core.Forward;
using Kubism.Core.Logs;
using Kubism.Desktop.Services;
using Kubism.Desktop.Terminal;

namespace Kubism.Desktop.ViewModels;

public abstract partial class DockItemViewModel : ViewModelBase, IClosable
{
    public abstract string Title { get; }
    public abstract string Kind { get; }
    public abstract ValueTask DisposeAsync();
}

/// <summary>Strips ANSI escape sequences and records the colors so the log view can render them.</summary>
public static partial class Ansi
{
    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Csi();

    public static string Strip(string s) => s.Contains('\u001b') ? Csi().Replace(s, "") : s;

    /// <summary>Segments of (text, foreground palette index or -1) from SGR sequences.</summary>
    public static List<(string text, int fg)> Segments(string s)
    {
        var result = new List<(string, int)>();
        if (!s.Contains('\u001b')) { result.Add((s, -1)); return result; }
        var fg = -1; var pos = 0;
        foreach (Match m in Csi().Matches(s))
        {
            if (m.Index > pos) result.Add((s[pos..m.Index], fg));
            pos = m.Index + m.Length;
            if (m.Value.EndsWith('m'))
            {
                var parts = m.Value[2..^1].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var v) ? v : 0).ToList();
                if (parts.Count == 0) parts.Add(0);
                for (var i = 0; i < parts.Count; i++)
                {
                    var v = parts[i];
                    if (v == 0 || v == 39) fg = -1;
                    else if (v is >= 30 and <= 37) fg = v - 30;
                    else if (v is >= 90 and <= 97) fg = v - 90 + 8;
                    else if (v == 38 && i + 2 < parts.Count && parts[i + 1] == 5) { fg = parts[i + 2]; i += 2; }
                }
            }
        }
        if (pos < s.Length) result.Add((s[pos..], fg));
        return result;
    }
}

public sealed record LogColorSpan(int Start, int Length, int Fg);

/// <summary>A log tab: follow, search with highlight, regex, wrap, pause, clear, download. Rendering is done by an AvaloniaEdit view.</summary>
public sealed partial class LogViewModel : DockItemViewModel
{
    private readonly WorkspaceViewModel _ws;
    private readonly LogBuffer _buffer;
    private readonly Func<ValueTask> _dispose;
    private readonly IUiThread _ui;
    private long _renderedSeq;
    private DateTimeOffset? _lastTime;
    private int _flushQueued;

    public LogViewModel(WorkspaceViewModel ws, string title, LogBuffer buffer, Func<ValueTask> dispose, bool multiSource)
    {
        _ws = ws; _title = title; _buffer = buffer; _dispose = dispose; _ui = ws.Ui; MultiSource = multiSource;
        buffer.Changed += QueueFlush;
    }

    private readonly string _title;
    public override string Title => _title;
    public override string Kind => "Logs";
    public bool MultiSource { get; }
    public LogBuffer Buffer => _buffer;

    [ObservableProperty] private bool _wrap;
    [ObservableProperty] private bool _showTimestamps;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _prettyJson;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private string _matchInfo = "";
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private string _state = "";

    /// <summary>Raised on the UI thread with text to append, or a full replacement.</summary>
    public event Action<string, IReadOnlyList<LogColorSpan>, bool>? TextChanged;
    public event Action<int>? TrimLines;

    partial void OnPausedChanged(bool value) { if (!value) QueueFlush(); }
    partial void OnShowTimestampsChanged(bool value) => Rebuild();
    partial void OnPrettyJsonChanged(bool value) => Rebuild();

    private void QueueFlush()
    {
        if (Paused) return; // buffering continues in the ring buffer
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1) return;
        _ = Task.Delay(100).ContinueWith(_ => _ui.Post(() =>
        {
            Interlocked.Exchange(ref _flushQueued, 0);
            // A flush queued just before the pause must not show what arrived since: resuming queues a new one.
            if (!Paused) Flush();
        }), TaskScheduler.Default);
    }

    public string FormatLine(LogLine l)
    {
        var text = l.Text;
        if (PrettyJson && text.TrimStart().StartsWith('{'))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject j)
                {
                    var level = j["level"]?.ToString() ?? j["severity"]?.ToString() ?? "";
                    var msg = j["msg"]?.ToString() ?? j["message"]?.ToString() ?? "";
                    var rest = string.Join(" ", j.Where(kv => kv.Key is not ("level" or "severity" or "msg" or "message" or "time" or "ts" or "timestamp")).Select(kv => $"{kv.Key}={kv.Value}"));
                    text = $"{level.ToUpperInvariant(),-5} {msg} {rest}".TrimEnd();
                }
            }
            catch { }
        }
        var prefix = MultiSource ? $"[{l.Source}] " : "";
        var ts = ShowTimestamps && l.Time is { } t ? t.ToString("HH:mm:ss.fff") + " " : "";
        return ts + prefix + text;
    }

    private void Flush()
    {
        var snapshot = _buffer.Snapshot();
        LineCount = snapshot.Count;
        var fresh = snapshot.Where(l => l.Seq > _renderedSeq).ToList();
        if (fresh.Count == 0) return;
        // Out-of-order arrival (merged streams) or buffer trimming needs a full rebuild.
        var outOfOrder = fresh.Any(l => l.Time is { } t && _lastTime is { } last && t < last);
        if (outOfOrder) { Rebuild(); return; }
        var sb = new StringBuilder(); var spans = new List<LogColorSpan>();
        foreach (var l in fresh) AppendLine(sb, spans, l);
        _renderedSeq = fresh[^1].Seq; _lastTime = fresh.LastOrDefault(l => l.Time is not null).Time ?? _lastTime;
        TextChanged?.Invoke(sb.ToString(), spans, false);
        if (snapshot.Count < _renderedLines + fresh.Count) { }
        _renderedLines += fresh.Count;
        if (_renderedLines > _buffer.Capacity + 100) { TrimLines?.Invoke(_renderedLines - _buffer.Capacity); _renderedLines = _buffer.Capacity; }
        State = Paused ? "paused" : "following";
    }

    private int _renderedLines;

    private void AppendLine(StringBuilder sb, List<LogColorSpan> spans, LogLine l)
    {
        var line = FormatLine(l);
        foreach (var (text, fg) in Ansi.Segments(line))
        {
            if (fg >= 0) spans.Add(new LogColorSpan(sb.Length, text.Length, fg));
            sb.Append(text);
        }
        sb.Append('\n');
    }

    public void Rebuild()
    {
        var snapshot = _buffer.Snapshot();
        var sb = new StringBuilder(); var spans = new List<LogColorSpan>();
        foreach (var l in snapshot) AppendLine(sb, spans, l);
        _renderedSeq = snapshot.Count == 0 ? 0 : snapshot.Max(l => l.Seq);
        _lastTime = snapshot.LastOrDefault(l => l.Time is not null).Time;
        _renderedLines = snapshot.Count;
        LineCount = snapshot.Count;
        TextChanged?.Invoke(sb.ToString(), spans, true);
    }

    [RelayCommand]
    private void Clear() { _buffer.Clear(); _renderedSeq = 0; _renderedLines = 0; _lastTime = null; TextChanged?.Invoke("", [], true); LineCount = 0; }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var path = await _ws.Dialogs.SaveFileAsync("Save log", $"{Title.Replace('/', '_')}.log");
        if (path is null) return;
        await File.WriteAllLinesAsync(path, _buffer.Snapshot().Select(l => (l.Time is { } t ? t.ToString("O") + " " : "") + (MultiSource ? $"[{l.Source}] " : "") + Ansi.Strip(l.Text)));
    }

    /// <summary>All matches (index, length) in the given text, or empty on an invalid regex.</summary>
    public IReadOnlyList<(int index, int length)> FindAll(string text)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrEmpty(Search)) { MatchInfo = ""; return result; }
        try
        {
            if (UseRegex)
                foreach (Match m in Regex.Matches(text, Search, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) { if (m.Length > 0) result.Add((m.Index, m.Length)); }
            else
            {
                var i = 0;
                while ((i = text.IndexOf(Search, i, StringComparison.OrdinalIgnoreCase)) >= 0) { result.Add((i, Search.Length)); i += Search.Length; }
            }
            MatchInfo = result.Count == 0 ? "no match" : $"{result.Count} matches";
        }
        catch (ArgumentException) { MatchInfo = "invalid regex"; }
        catch (RegexMatchTimeoutException) { MatchInfo = "regex too slow"; }
        return result;
    }

    public override async ValueTask DisposeAsync()
    {
        _buffer.Changed -= QueueFlush;
        await _dispose().ConfigureAwait(false);
    }
}

public sealed partial class TerminalViewModel : DockItemViewModel
{
    private readonly WorkspaceViewModel _ws;
    private ExecSession? _session;
    private readonly string _title;
    private readonly Func<CancellationToken, Task<ExecSession>> _open;

    public TerminalViewModel(WorkspaceViewModel ws, string title, Func<CancellationToken, Task<ExecSession>> open)
    {
        _ws = ws; _title = title; _open = open;
        Buffer = new TerminalBuffer(100, 30);
        Buffer.Response += s => { if (_session is { IsRunning: true }) _ = _session.SendInputAsync(s); };
    }

    public override string Title => _title;
    public override string Kind => "Terminal";
    public TerminalBuffer Buffer { get; }
    [ObservableProperty] private string _status = "Connecting…";
    [ObservableProperty] private bool _exited;
    [ObservableProperty] private string? _exitMessage;
    public event Action? Redraw;

    public async Task StartAsync()
    {
        Exited = false; ExitMessage = null; Status = "Connecting…";
        try
        {
            _session = await _open(_ws.Session.SessionToken);
            _session.Output += (data, ch) => _ws.Ui.Post(() => { Buffer.Write(data); Redraw?.Invoke(); });
            _session.Exited += r => _ws.Ui.Post(() =>
            {
                Exited = true; Status = "Disconnected";
                ExitMessage = r.Success ? "Session ended" : r.Message ?? "Session failed";
                Redraw?.Invoke();
            });
            Status = "Connected";
            await _session.ResizeAsync(Buffer.Cols, Buffer.Rows);
        }
        catch (Exception ex)
        {
            Exited = true; Status = "Failed"; ExitMessage = ex.Message;
            Buffer.Write("\r\n\u001b[31m" + ex.Message + "\u001b[0m\r\n");
        }
    }

    public Task CopyAsync(string text) => _ws.Clipboard.SetTextAsync(text);

    public async Task SendAsync(string text)
    {
        if (_session is { IsRunning: true }) await _session.SendInputAsync(text);
    }

    public async Task ResizeAsync(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        if (_session is { IsRunning: true }) await _session.ResizeAsync(cols, rows);
        Redraw?.Invoke();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
        Buffer.Write("\r\n--- reconnecting ---\r\n");
        await StartAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }
}

public sealed partial class PortForwardsViewModel : DockItemViewModel
{
    private readonly WorkspaceViewModel _ws;

    public PortForwardsViewModel(WorkspaceViewModel ws)
    {
        _ws = ws;
        ws.Session.PortForwards.Changed += OnChanged;
        Rebuild();
    }

    public override string Title => "Port forwards".T();
    public override string Kind => "PortForwards";
    public ObservableCollection<PortForwardRow> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;

    private void OnChanged() => _ws.Ui.Post(Rebuild);

    private void Rebuild()
    {
        if (_ws.Session.PortForwards is not { } manager) return;   // session already torn down
        Items.Clear();
        foreach (var f in manager.Forwards) Items.Add(new PortForwardRow(f, this));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task StopAsync(PortForward f)
    {
        SetKeep(f, false);
        await _ws.Session.PortForwards.StopAsync(f.Id);
    }

    /// <summary>"Keep" forwards are stored per cluster and restarted when the cluster is connected again.</summary>
    public void SetKeep(PortForward f, bool keep)
    {
        var meta = _ws.Session.Metadata;
        meta.KeptForwards.RemoveAll(k => k.Namespace == f.Target.Namespace && k.Name == f.Target.Name && k.RemotePort == f.Target.RemotePort && k.Kind == f.Target.Kind.ToString());
        if (keep) meta.KeptForwards.Add(new KeptForward { Kind = f.Target.Kind.ToString(), Namespace = f.Target.Namespace, Name = f.Target.Name, RemotePort = f.Target.RemotePort, LocalPort = f.LocalPort });
        _ws.Main.Settings.SetMetadata(_ws.Id, meta);
    }
    public Task CopyUrlAsync(PortForward f) => _ws.Clipboard.SetTextAsync(f.Url);
    public void Open(PortForward f) => _ws.Main.Browser.Open(f.Url);

    public override ValueTask DisposeAsync()
    {
        try { _ws.Session.PortForwards.Changed -= OnChanged; } catch { }
        return ValueTask.CompletedTask;
    }
}

public sealed partial class PortForwardRow(PortForward forward, PortForwardsViewModel owner) : ViewModelBase
{
    public PortForward Forward { get; } = forward;
    public string Target => $"{Forward.Target.Kind.ToString().ToLowerInvariant()}/{Forward.Target.Namespace}/{Forward.Target.Name}:{Forward.Target.RemotePort}";
    public string Local => $"127.0.0.1:{Forward.LocalPort}";
    public string Info => Forward.LastError is { } e ? $"error: {e}" : $"{Forward.ActiveConnections} active, {Forward.TotalConnections} total" + (Forward.ResolvedPod is { } p ? $" → {p}" : "");
    public bool Keep
    {
        get => Forward.Keep;
        set { Forward.Keep = value; owner.SetKeep(Forward, value); OnPropertyChanged(); }
    }
    [RelayCommand] private Task Stop() => owner.StopAsync(Forward);
    [RelayCommand] private Task CopyUrl() => owner.CopyUrlAsync(Forward);
    [RelayCommand] private void Open() => owner.Open(Forward);
}

public sealed partial class DockViewModel : ViewModelBase
{
    public ObservableCollection<DockItemViewModel> Items { get; } = [];
    [ObservableProperty] private DockItemViewModel? _selected;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private double _height = 260;

    public void Add(DockItemViewModel item)
    {
        Items.Add(item); Selected = item; IsOpen = true;
    }

    public async Task CloseAsync(DockItemViewModel item)
    {
        Items.Remove(item);
        await item.DisposeAsync();
        if (Items.Count == 0) IsOpen = false; else Selected ??= Items[^1];
    }

    public async Task CloseAllAsync()
    {
        foreach (var i in Items.ToList()) { try { await i.DisposeAsync(); } catch { } }
        Items.Clear(); IsOpen = false;
    }

    public T? Find<T>() where T : DockItemViewModel => Items.OfType<T>().FirstOrDefault();
}
