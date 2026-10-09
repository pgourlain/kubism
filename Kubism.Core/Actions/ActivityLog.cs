using System.Text;

namespace Kubism.Core.Actions;

public sealed record ActivityEntry(DateTimeOffset Time, string Action, string Target, bool Success, string? Message);

/// <summary>What this app did on a cluster. Never contains secret values.</summary>
public sealed class ActivityLog
{
    private readonly List<ActivityEntry> _entries = [];
    private readonly object _gate = new();
    public event Action<ActivityEntry>? Added;

    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public void Add(string action, string target, bool success, string? message = null)
    {
        var e = new ActivityEntry(DateTimeOffset.Now, action, target, success, message);
        lock (_gate) _entries.Add(e);
        Added?.Invoke(e);
    }

    public string ExportCsv()
    {
        var sb = new StringBuilder("time,action,target,result,message\n");
        foreach (var e in Entries)
            sb.Append(e.Time.ToString("O")).Append(',').Append(Q(e.Action)).Append(',').Append(Q(e.Target)).Append(',')
              .Append(e.Success ? "ok" : "failed").Append(',').Append(Q(e.Message ?? "")).Append('\n');
        return sb.ToString();
    }

    private static string Q(string s) => '"' + s.Replace("\"", "\"\"") + '"';
}
