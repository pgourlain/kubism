using System.Text;

namespace Kubism.Core;

/// <summary>Line diff (longest common subsequence). "- " lines are only in <c>a</c>, "+ " lines only in <c>b</c>, "  " lines are common.</summary>
public static class LineDiff
{
    public static string Unified(string a, string b, int? context = null)
    {
        var ops = Ops(Split(a), Split(b));
        if (context is null) return string.Concat(ops.Select(o => o + "\n"));
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
            if (ops[i][0] != ' ')
                for (var j = Math.Max(0, i - context.Value); j <= Math.Min(ops.Count - 1, i + context.Value); j++) keep[j] = true;
        var sb = new StringBuilder();
        var skipped = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (keep[i]) { sb.AppendLine(ops[i]); skipped = false; }
            else if (!skipped) { sb.AppendLine("  …"); skipped = true; }
        }
        return sb.ToString();
    }

    public static (int added, int removed) Count(string diff)
    {
        int add = 0, rem = 0;
        foreach (var l in diff.Split('\n'))
        {
            if (l.StartsWith("+ ")) add++;
            else if (l.StartsWith("- ")) rem++;
        }
        return (add, rem);
    }

    private static string[] Split(string s) => s.Length == 0 ? [] : s.Replace("\r", "").TrimEnd('\n').Split('\n');

    private static List<string> Ops(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { ops.Add("  " + a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) ops.Add("- " + a[x++]);
            else ops.Add("+ " + b[y++]);
        }
        for (; x < a.Length; x++) ops.Add("- " + a[x]);
        for (; y < b.Length; y++) ops.Add("+ " + b[y]);
        return ops;
    }
}
