using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Helm;

/// <summary>
/// What to upgrade a release to. <see cref="Chart"/> is a chart reference helm understands (<c>repo/name</c>, <c>oci://…</c>, a path); empty means
/// "the chart already installed", rebuilt from the release (<see cref="ReleaseChart"/>). <see cref="ValuesYaml"/> replaces the values the user supplied:
/// what is removed from it returns to the chart's default.
/// </summary>
public sealed record HelmUpgradeRequest(string Namespace, string Release, string Chart, string? Version, string ValuesYaml, bool Wait, bool Atomic, JsonObject? ReleaseChart = null)
{
    public bool UsesInstalledChart => string.IsNullOrWhiteSpace(Chart);
}

/// <summary>Builds the helm command lines for an upgrade or its preview, and the chart on disk when the installed one is reused. Nothing here touches the cluster.</summary>
public static class HelmUpgrade
{
    /// <summary>Why the installed chart cannot be rebuilt from the release secret, or null when it can. Subcharts are not stored in a release, so rebuilding would drop their objects.</summary>
    public static string? CannotRebuildReason(JsonObject? chart)
    {
        if (chart is null) return "The release's chart could not be read";
        if (HelmChartExporter.HasDependencies(chart)) return "This chart has dependencies (subcharts), which Helm does not keep in the release: give a chart reference instead, or the upgrade would remove their objects";
        if ((chart["templates"] as JsonArray)?.Count is null or 0) return "The release does not contain the chart's templates";
        return null;
    }

    /// <summary>Command line for <c>helm upgrade</c> (<paramref name="template"/> false) or <c>helm template</c> (a local render for the preview), given where the chart and the values file are.</summary>
    public static IReadOnlyList<string> BuildArgs(HelmUpgradeRequest r, string chartRef, string valuesPath, bool template)
    {
        var args = new List<string> { template ? "template" : "upgrade", r.Release, chartRef, "-n", r.Namespace, "-f", valuesPath };
        if (template) { args.Add("--no-hooks"); args.Add("--is-upgrade"); }
        else
        {
            args.Add("--reset-values");   // the values file is the whole set of user values, not an addition to the previous ones
            if (r.Atomic) args.Add("--atomic"); else if (r.Wait) args.Add("--wait");
        }
        if (!r.UsesInstalledChart && !string.IsNullOrWhiteSpace(r.Version)) { args.Add("--version"); args.Add(r.Version!); }
        return args;
    }

    /// <summary>A scratch folder with the values file and, when reusing the installed chart, the rebuilt chart. Deleted on dispose.</summary>
    public sealed class Workspace : IDisposable
    {
        private readonly string _dir;
        public string ChartRef { get; }
        public string ValuesPath { get; }

        public Workspace(HelmUpgradeRequest r)
        {
            _dir = Path.Combine(Path.GetTempPath(), "kubism-helm-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(_dir);
            ValuesPath = Path.Combine(_dir, "values.yaml");
            File.WriteAllText(ValuesPath, r.ValuesYaml);
            if (r.UsesInstalledChart)
            {
                if (CannotRebuildReason(r.ReleaseChart) is { } why) { Dispose(); throw new InvalidOperationException(why); }
                var chartDir = Path.Combine(_dir, r.ReleaseChart!["metadata"]?["name"]?.ToString() is { Length: > 0 } n && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? n : "chart");
                HelmChartExporter.Write(r.ReleaseChart!, chartDir);
                ChartRef = chartDir;
            }
            else ChartRef = r.Chart;
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Renders the chart with the new values locally (<c>helm template</c>, no hooks) and diffs it against the manifest of the installed release.
    /// Charts that look things up in the cluster or branch on the cluster's API versions can render a little differently from the real upgrade.
    /// </summary>
    public static async Task<(string Diff, bool Changed, string Rendered)> PreviewAsync(IHelmRunner helm, HelmUpgradeRequest r, string? currentManifest, CancellationToken ct)
    {
        using var ws = new Workspace(r);
        var rendered = await helm.RunAsync(BuildArgs(r, ws.ChartRef, ws.ValuesPath, template: true), ct).ConfigureAwait(false);
        var a = Normalize(currentManifest ?? ""); var b = Normalize(rendered);
        if (a == b) return ("", false, rendered);
        return (LineDiff.Unified(a, b, 3), true, rendered);
    }

    /// <summary>Drops the blank lines and trailing spaces helm adds differently between a stored manifest and a render.</summary>
    private static string Normalize(string manifest) =>
        string.Join('\n', manifest.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
}
