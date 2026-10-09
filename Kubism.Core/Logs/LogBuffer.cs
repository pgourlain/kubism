namespace Kubism.Core.Logs;

public readonly record struct LogLine(DateTimeOffset? Time, string Text, string Source, long Seq);

/// <summary>Bounded buffer of log lines, kept ordered by timestamp when lines from several streams are merged.</summary>
public sealed class LogBuffer(int capacity)
{
    private readonly List<LogLine> _lines = [];
    private readonly object _gate = new();
    private int _head;
    private long _seq;

    public int Capacity { get; } = Math.Max(1, capacity);
    public long Dropped { get; private set; }
    public long TotalAdded { get; private set; }
    public event Action? Changed;

    public int Count { get { lock (_gate) return _lines.Count - _head; } }

    public void Add(DateTimeOffset? time, string text, string source)
    {
        lock (_gate)
        {
            var line = new LogLine(time, text, source, ++_seq);
            var idx = _lines.Count;
            // Merge-by-timestamp: walk back while the previous line is newer (bounded to recent lines).
            if (time is not null)
            {
                var steps = 0;
                while (idx > _head && steps < 2000 && _lines[idx - 1].Time is { } t && t > time) { idx--; steps++; }
            }
            _lines.Insert(idx, line);
            TotalAdded++;
            while (_lines.Count - _head > Capacity) { _head++; Dropped++; }
            if (_head > Capacity) { _lines.RemoveRange(0, _head); _head = 0; }
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.GetRange(_head, _lines.Count - _head);
    }

    public LogLine? Get(int index)
    {
        lock (_gate) return index >= 0 && index < _lines.Count - _head ? _lines[_head + index] : null;
    }

    public void Clear()
    {
        lock (_gate) { _lines.Clear(); _head = 0; }
        Changed?.Invoke();
    }
}
