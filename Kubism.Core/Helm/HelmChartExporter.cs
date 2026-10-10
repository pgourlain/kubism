using System.Text.Json.Nodes;

namespace Kubism.Core.Helm;

/// <summary>
/// Writes the chart stored inside a Helm release (its <c>chart</c> object: metadata, values, templates, files, schema) back to a folder helm can use.
/// A release keeps no subcharts, so charts with dependencies cannot be rebuilt (see <see cref="HelmUpgrade.CannotRebuildReason"/>).
/// </summary>
public static class HelmChartExporter
{
    public static bool HasDependencies(JsonObject chart) => (chart["metadata"]?["dependencies"] as JsonArray)?.Count > 0;

    public static void Write(JsonObject chart, string dir)
    {
        Directory.CreateDirectory(dir);
        var metadata = chart["metadata"] as JsonObject ?? throw new InvalidDataException("the release's chart has no metadata");
        File.WriteAllText(Path.Combine(dir, "Chart.yaml"), YamlJson.ToYaml(metadata));
        File.WriteAllText(Path.Combine(dir, "values.yaml"), YamlJson.ToYaml(chart["values"] as JsonObject ?? new JsonObject()));
        foreach (var group in new[] { "templates", "files" })
            foreach (var f in chart[group] as JsonArray ?? [])
                if (f is JsonObject file && file["name"]?.ToString() is { Length: > 0 } name)
                    WriteFile(dir, name, Convert.FromBase64String(file["data"]?.ToString() ?? ""));
        if (chart["schema"]?.ToString() is { Length: > 0 } schema) WriteFile(dir, "values.schema.json", Convert.FromBase64String(schema));
    }

    /// <summary>Release data is untrusted: a file name must stay inside the chart folder.</summary>
    private static void WriteFile(string dir, string name, byte[] data)
    {
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(name) || !path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"chart file '{name}' points outside the chart");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }
}
