namespace Kubism.Core.Util;

/// <summary>
/// Left-to-right layout in fixed columns with barycentre ordering: a few sweeps right then left put each node near the average row of its
/// neighbours, which keeps edge crossings low. Pure, shared by the RBAC chain and the network graph; drawing is the UI's job.
/// </summary>
public static class ColumnLayout
{
    /// <summary>Sets each node's row (dense per column) and returns the tallest column's height.</summary>
    public static int Arrange<T>(IReadOnlyList<T> nodes, int columns, Func<T, string> id, Func<T, int> column, Action<T, int> setRow, Func<T, string> label,
        IEnumerable<(string From, string To)> edges, Func<T, int>? group = null)
    {
        if (nodes.Count == 0) return 0;
        var byId = nodes.ToDictionary(id);
        var neighbours = nodes.ToDictionary(id, _ => new List<string>());
        foreach (var (from, to) in edges)
        {
            if (!neighbours.ContainsKey(from) || !neighbours.ContainsKey(to)) continue;
            neighbours[from].Add(to); neighbours[to].Add(from);
        }
        var cols = Enumerable.Range(0, columns).Select(c => nodes.Where(n => column(n) == c).OrderBy(n => group?.Invoke(n) ?? 0).ThenBy(label, StringComparer.Ordinal).ToList()).ToArray();
        var rows = new Dictionary<string, int>();
        void Number(List<T> col) { for (var i = 0; i < col.Count; i++) rows[id(col[i])] = i; }
        foreach (var c in cols) Number(c);
        void Sweep(int col, int from)
        {
            var keyed = cols[col].Select(n => (n, key: neighbours[id(n)].Where(x => column(byId[x]) == from).Select(x => (double)rows[x]).DefaultIfEmpty(double.MaxValue / 4).Average())).ToList();
            cols[col] = keyed.OrderBy(k => k.key).ThenBy(k => label(k.n), StringComparer.Ordinal).Select(k => k.n).ToList();
            Number(cols[col]);
        }
        for (var pass = 0; pass < 4; pass++)
        {
            for (var c = 1; c < columns; c++) Sweep(c, c - 1);
            for (var c = columns - 2; c >= 0; c--) Sweep(c, c + 1);
        }
        foreach (var n in nodes) setRow(n, rows[id(n)]);
        return cols.Max(c => c.Count);
    }
}
