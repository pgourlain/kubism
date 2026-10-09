using Kubism.Core.Api;
using Kubism.Core.Cache;

namespace Kubism.Core.Table;

public sealed record ColumnDef(string Name, string Type, int Priority, bool IsAge, int Index);

/// <summary>Server-side Table columns per kind, with fallback columns (Name, Namespace, Age) when the server refuses Table.</summary>
public static class TableService
{
    /// <summary>Columns for a list. Index is the position in the server cells (-1 for computed fallbacks).</summary>
    public static IReadOnlyList<ColumnDef> Columns(IReadOnlyList<TableColumn>? server, bool namespaced)
    {
        if (server is { Count: > 0 })
        {
            var cols = server.Select((c, i) => new ColumnDef(c.Name, c.Type, c.Priority, c.Name == "Age", i)).ToList();
            if (namespaced && !cols.Any(c => c.Name == "Namespace"))
                cols.Insert(1, new ColumnDef("Namespace", "string", 0, false, -2));
            return cols;
        }
        var fb = new List<ColumnDef> { new("Name", "string", 0, false, -1) };
        if (namespaced) fb.Add(new ColumnDef("Namespace", "string", 0, false, -2));
        fb.Add(new ColumnDef("Age", "string", 0, true, -3));
        return fb;
    }

    public static string Cell(CacheItem item, ColumnDef col, TimeSpan skew = default, DateTimeOffset? now = null)
    {
        if (col.IsAge || col.Index == -3) return KubeTime.Age(item.Object.CreationTimestamp, skew, now);
        return col.Index switch
        {
            -1 => item.Object.Name,
            -2 => item.Object.Namespace ?? "",
            >= 0 when item.Cells is { } cells && col.Index < cells.Count => cells[col.Index] ?? "",
            _ => "",
        };
    }

    /// <summary>Comparable key: numbers numerically, Age by creation time (newest = smallest age), otherwise natural string order.</summary>
    public static IComparable SortKey(CacheItem item, ColumnDef col)
    {
        if (col.IsAge) return -(item.Object.CreationTimestamp?.ToUnixTimeSeconds() ?? 0);
        var text = Cell(item, col);
        if (col.Type is "integer" or "number" || double.TryParse(text, out _))
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return new NaturalString(text);
    }

    private readonly record struct NaturalString(string Value) : IComparable
    {
        public int CompareTo(object? obj)
        {
            if (obj is not NaturalString other) return 1;
            return Compare(Value, other.Value);
        }

        public static int Compare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    var c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>Filter: free text matches name; "key=value" matches labels.</summary>
    public static bool Matches(CacheItem item, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        foreach (var term in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Contains('='))
            {
                var kv = term.Split('=', 2);
                if (!item.Object.Labels.TryGetValue(kv[0], out var v) || (kv[1].Length > 0 && v != kv[1])) return false;
            }
            else if (!item.Object.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
