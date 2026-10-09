using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Views;

namespace Kubism.Core.RightSizing;

/// <summary>Where the truth really lives: plain YAML, Helm values and Kustomize patches, plus CSV/Markdown reports and a diff for the preview.</summary>
public static class Snippets
{
    private static JsonObject Resources(RecommendedResources r)
    {
        var req = new JsonObject { ["cpu"] = Quantities.FormatCpu(r.CpuRequest), ["memory"] = Quantities.FormatMemoryMi(r.MemoryRequest) };
        var res = new JsonObject { ["requests"] = req };
        var lim = new JsonObject();
        if (r.CpuLimit is { } cl) lim["cpu"] = Quantities.FormatCpu(cl);
        if (r.MemoryLimit is { } ml) lim["memory"] = Quantities.FormatMemoryMi(ml);
        if (lim.Count > 0) res["limits"] = lim;
        return res;
    }

    public static string Yaml(IEnumerable<ContainerReport> containers)
    {
        var sb = new StringBuilder();
        foreach (var w in containers.Where(c => c.Recommended is not null).GroupBy(c => (c.Namespace, c.WorkloadKind, c.WorkloadName)))
        {
            var list = new JsonArray();
            foreach (var c in w) list.Add((JsonNode?)new JsonObject { ["name"] = c.Container, ["resources"] = Resources(c.Recommended!) });
            var doc = new JsonObject
            {
                ["apiVersion"] = "apps/v1", ["kind"] = w.Key.WorkloadKind,
                ["metadata"] = new JsonObject { ["name"] = w.Key.WorkloadName, ["namespace"] = w.Key.Namespace },
                ["spec"] = new JsonObject { ["template"] = new JsonObject { ["spec"] = new JsonObject { ["containers"] = list } } },
            };
            sb.Append("---\n").Append(YamlJson.ToYaml(doc));
        }
        return sb.ToString();
    }

    public static string HelmValues(IEnumerable<ContainerReport> containers)
    {
        var sb = new StringBuilder();
        foreach (var w in containers.Where(c => c.Recommended is not null).GroupBy(c => (c.Namespace, c.WorkloadName)))
        {
            sb.Append("# ").Append(w.Key.Namespace).Append('/').Append(w.Key.WorkloadName).Append('\n');
            foreach (var c in w)
            {
                sb.Append("# container: ").Append(c.Container).Append('\n');
                sb.Append(YamlJson.ToYaml(new JsonObject { ["resources"] = Resources(c.Recommended!) }));
            }
        }
        return sb.ToString();
    }

    public static string KustomizePatch(IEnumerable<ContainerReport> containers)
    {
        var sb = new StringBuilder();
        foreach (var w in containers.Where(c => c.Recommended is not null).GroupBy(c => (c.Namespace, c.WorkloadKind, c.WorkloadName)))
        {
            sb.Append("- target:\n    kind: ").Append(w.Key.WorkloadKind).Append("\n    name: ").Append(w.Key.WorkloadName).Append("\n  patch: |-\n");
            var list = new JsonArray();
            foreach (var c in w) list.Add((JsonNode?)new JsonObject { ["name"] = c.Container, ["resources"] = Resources(c.Recommended!) });
            var patch = new JsonObject
            {
                ["apiVersion"] = "apps/v1", ["kind"] = w.Key.WorkloadKind, ["metadata"] = new JsonObject { ["name"] = w.Key.WorkloadName },
                ["spec"] = new JsonObject { ["template"] = new JsonObject { ["spec"] = new JsonObject { ["containers"] = list } } },
            };
            foreach (var line in YamlJson.ToYaml(patch).TrimEnd().Split('\n')) sb.Append("    ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Line diff of the resources block, current → recommended.</summary>
    public static string Diff(ContainerReport c)
    {
        if (c.Recommended is not { } r) return "";
        string Q(double? v, bool cpu) => v is null ? "(none)" : cpu ? Quantities.FormatCpu(v.Value) : Quantities.FormatMemoryMi(v.Value);
        var lines = new List<string> { $"  container: {c.Container}" };
        void Row(string label, string was, string now) { if (was == now) lines.Add($"    {label}: {now}"); else { lines.Add($"-   {label}: {was}"); lines.Add($"+   {label}: {now}"); } }
        Row("requests.cpu", Q(c.CpuRequest, true), Q(r.CpuRequest, true));
        Row("requests.memory", Q(c.MemoryRequest, false), Q(r.MemoryRequest, false));
        Row("limits.cpu", Q(c.CpuLimit, true), Q(r.CpuLimit, true));
        Row("limits.memory", Q(c.MemoryLimit, false), Q(r.MemoryLimit, false));
        return string.Join("\n", lines);
    }

    public static string Csv(RightSizingReport report)
    {
        var sb = new StringBuilder("cluster_source,namespace,kind,workload,container,replicas,cpu_request,cpu_limit,cpu_p95,cpu_max,mem_request,mem_limit,mem_p95,mem_max,throttled,oomkills,verdict,rec_cpu_request,rec_mem_request,rec_mem_limit,waste_cpu_cores,waste_mem_gib\n");
        string N(double? v) => v is null ? "" : v.Value.ToString("0.####", CultureInfo.InvariantCulture);
        foreach (var c in report.Containers)
            sb.Append(string.Join(",", report.Source, c.Namespace, c.WorkloadKind, c.WorkloadName, c.Container, c.Replicas, N(c.CpuRequest), N(c.CpuLimit), N(c.CpuP95), N(c.CpuMax),
                N(c.MemoryRequest / (1024 * 1024)), N(c.MemoryLimit / (1024 * 1024)), N(c.MemoryP95 / (1024 * 1024)), N(c.MemoryMax / (1024 * 1024)), N(c.ThrottledRatio), c.OomKills, c.Primary,
                N(c.Recommended?.CpuRequest), N(c.Recommended is null ? null : c.Recommended.MemoryRequest / (1024 * 1024)), N(c.Recommended?.MemoryLimit / (1024 * 1024)),
                N(c.WasteCpuCores), N(c.WasteMemoryBytes / (1024.0 * 1024 * 1024)))).Append('\n');
        return sb.ToString();
    }

    public static string Markdown(RightSizingReport report, string? cluster = null)
    {
        var sb = new StringBuilder();
        sb.Append("# Right-sizing report").Append(cluster is null ? "" : $" — {cluster}").Append("\n\n");
        sb.Append($"- Source: {report.Source}\n- Window: {report.Window.TotalDays:0.#} days (coverage {report.Coverage:P0}, {report.DataHours:0.#} h of data)\n- Strategy: {report.Strategy}\n");
        sb.Append($"- CPU requested {Quantities.FormatCpu(report.CpuRequested)} vs p95 used {Quantities.FormatCpu(report.CpuUsed)}; waste {Quantities.FormatCpu(report.WasteCpu)}\n");
        sb.Append($"- Memory requested {Quantities.FormatBytes(report.MemoryRequested)} vs p95 used {Quantities.FormatBytes(report.MemoryUsed)}; waste {Quantities.FormatBytes(report.WasteMemory)}\n\n");
        sb.Append("| Namespace | Workload | Container | Verdict | CPU req → rec | Memory req → rec |\n|---|---|---|---|---|---|\n");
        foreach (var c in report.Containers.OrderByDescending(c => c.WasteCpuCores))
            sb.Append($"| {c.Namespace} | {c.WorkloadKind}/{c.WorkloadName} | {c.Container} | {c.Primary} | {(c.CpuRequest is null ? "-" : Quantities.FormatCpu(c.CpuRequest.Value))} → {(c.Recommended is null ? "-" : Quantities.FormatCpu(c.Recommended.CpuRequest))} | {(c.MemoryRequest is null ? "-" : Quantities.FormatMemoryMi(c.MemoryRequest.Value))} → {(c.Recommended is null ? "-" : Quantities.FormatMemoryMi(c.Recommended.MemoryRequest))} |\n");
        return sb.ToString();
    }
}
