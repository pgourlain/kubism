using System.Globalization;
using System.Text;
using Kubism.Core.Views;

namespace Kubism.Core.Images;

/// <summary>Where an image reference differs between two clusters (or two namespaces) for one container of one workload.</summary>
/// <param name="Left">The references the left side runs (more than one while a rollout is in progress).</param>
/// <param name="VersionOnly">Both sides run the same repositories and only the tag or digest differs: the other side is behind or ahead.</param>
public sealed record ImageDiffEntry(string Kind, string? Namespace, string Name, string Container, CompareStatus Status, IReadOnlyList<string> Left, IReadOnlyList<string> Right, bool VersionOnly)
{
    public string Workload => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
    public string Display => $"{Workload} · {Container}";
}

public static class ImageComparison
{
    /// <summary>
    /// Compares what each workload container runs. A container is identified by workload kind, name and container name, and by the namespace
    /// when <paramref name="matchNamespace"/> is true (both sides read the same namespaces); otherwise by name alone.
    /// </summary>
    public static IReadOnlyList<ImageDiffEntry> Compare(IEnumerable<ImageRow> left, IEnumerable<ImageRow> right, bool matchNamespace = true)
    {
        var l = Index(left, matchNamespace);
        var r = Index(right, matchNamespace);
        var result = new List<ImageDiffEntry>();
        foreach (var key in l.Keys.Union(r.Keys).Order(Comparer<(string, string?, string, string)>.Create(Compare)))
        {
            var inL = l.TryGetValue(key, out var a); var inR = r.TryGetValue(key, out var b);
            var la = a?.Refs.Order(StringComparer.Ordinal).ToList() ?? []; var rb = b?.Refs.Order(StringComparer.Ordinal).ToList() ?? [];
            var status = !inR ? CompareStatus.OnlyLeft : !inL ? CompareStatus.OnlyRight : la.SequenceEqual(rb) ? CompareStatus.Same : CompareStatus.Different;
            var versionOnly = status == CompareStatus.Different
                && la.Select(x => ImageRef.Parse(x).Name).Distinct().Order(StringComparer.Ordinal).SequenceEqual(rb.Select(x => ImageRef.Parse(x).Name).Distinct().Order(StringComparer.Ordinal));
            var ns = matchNamespace ? key.Item2 : null;
            result.Add(new ImageDiffEntry(key.Item1, ns, key.Item3, key.Item4, status, la, rb, versionOnly));
        }
        return result;
    }

    private static int Compare((string, string?, string, string) x, (string, string?, string, string) y)
    {
        var c = string.CompareOrdinal(x.Item2, y.Item2); if (c != 0) return c;
        c = string.CompareOrdinal(x.Item3, y.Item3); if (c != 0) return c;
        c = string.CompareOrdinal(x.Item1, y.Item1); if (c != 0) return c;
        return string.CompareOrdinal(x.Item4, y.Item4);
    }

    private static Dictionary<(string, string?, string, string), RefSet> Index(IEnumerable<ImageRow> rows, bool matchNamespace)
    {
        var map = new Dictionary<(string, string?, string, string), RefSet>();
        foreach (var row in rows)
            foreach (var u in row.Users)
            {
                var key = (u.Kind, matchNamespace ? u.Namespace : null, u.Name, u.Role == "container" ? u.Container : $"{u.Container} ({u.Role})");
                if (!map.TryGetValue(key, out var set)) map[key] = set = new RefSet();
                set.Refs.Add(row.Reference);
            }
        return map;
    }

    private sealed class RefSet { public HashSet<string> Refs { get; } = []; }
}

/// <summary>CSV and Markdown of an image list, for a ticket or an audit. Names and versions only.</summary>
public static class ImageExport
{
    public static string Csv(IEnumerable<ImageRow> rows, string? cluster = null)
    {
        var sb = new StringBuilder("cluster,image,registry,repository,tag,digest,resolved_digests,pods,workloads,namespaces,pull_policies,latest,not_pinned,pull_failing,several_versions\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", Q(cluster ?? ""), Q(r.Reference), Q(r.Image.Registry), Q(r.Image.Repository), Q(r.Image.Tag ?? ""), Q(r.Image.Digest ?? ""), Q(string.Join(" ", r.ResolvedDigests)),
                    r.Pods.ToString(CultureInfo.InvariantCulture), r.Workloads.ToString(CultureInfo.InvariantCulture), Q(string.Join(" ", r.Namespaces)), Q(string.Join(" ", r.PullPolicies)),
                    Flag(r, ImageIssues.Latest), Flag(r, ImageIssues.NotPinned), Flag(r, ImageIssues.PullFailing), Flag(r, ImageIssues.MixedVersions))).Append('\n');
        return sb.ToString();
    }

    public static string Markdown(IReadOnlyList<ImageRow> rows, string? cluster = null)
    {
        var sb = new StringBuilder("# Images").Append(cluster is null ? "" : $" — {cluster}").Append("\n\n");
        sb.Append(rows.Count).Append(" image(s) in ").Append(rows.Select(r => r.Image.Name).Distinct().Count()).Append(" repositories. ")
          .Append(rows.Count(r => r.Issues.HasFlag(ImageIssues.Latest))).Append(" latest or untagged, ")
          .Append(rows.Count(r => r.Issues.HasFlag(ImageIssues.NotPinned))).Append(" not pinned by digest, ")
          .Append(rows.Count(r => r.Issues.HasFlag(ImageIssues.PullFailing))).Append(" failing to pull.\n\n");
        sb.Append("| Image | Pods | Workloads | Namespaces | To look at |\n|---|---|---|---|---|\n");
        foreach (var r in rows)
        {
            var issues = new List<string>();
            if (r.Issues.HasFlag(ImageIssues.PullFailing)) issues.Add("pull failing");
            if (r.Issues.HasFlag(ImageIssues.Latest)) issues.Add("latest or no tag");
            if (r.Issues.HasFlag(ImageIssues.MixedVersions)) issues.Add($"{r.Versions} versions");
            sb.Append("| `").Append(r.Reference.Replace("|", "\\|")).Append("` | ").Append(r.Pods).Append(" | ").Append(r.Workloads).Append(" | ").Append(r.Namespaces.Count).Append(" | ").Append(string.Join(", ", issues)).Append(" |\n");
        }
        return sb.ToString();
    }

    public static string CompareCsv(IEnumerable<ImageDiffEntry> entries, string left, string right)
    {
        var sb = new StringBuilder("status,kind,namespace,workload,container,").Append(Q(left)).Append(',').Append(Q(right)).Append(",version_only\n");
        foreach (var e in entries)
            sb.Append(string.Join(",", e.Status, e.Kind, Q(e.Namespace ?? ""), Q(e.Name), Q(e.Container), Q(string.Join(" ", e.Left)), Q(string.Join(" ", e.Right)), e.VersionOnly ? "true" : "false")).Append('\n');
        return sb.ToString();
    }

    private static string Flag(ImageRow r, ImageIssues f) => r.Issues.HasFlag(f) ? "true" : "false";

    private static string Q(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
