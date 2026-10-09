using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record DetailRow(string Label, string Value);
public sealed record DetailTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed class DetailSection(string title)
{
    public string Title { get; } = title;
    public List<DetailRow> Rows { get; } = [];
    public List<string> Chips { get; } = [];
    public DetailTable? Table { get; set; }
    public bool HasChips => Chips.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public bool HasTable => Table is { Rows.Count: > 0 };
}

/// <summary>Builds the Overview tab per kind. Secret references are shown as references, never as values.</summary>
public static class DetailSectionBuilder
{
    public static List<DetailSection> Build(KubeObject o, TimeSpan skew = default)
    {
        var list = new List<DetailSection> { Metadata(o, skew) };
        switch (o.Kind)
        {
            case "Pod": Pod(o, list); break;
            case "Deployment" or "StatefulSet" or "ReplicaSet" or "DaemonSet": Workload(o, list); break;
            case "Service": Service(o, list); break;
            case "Node": Node(o, list); break;
            case "CronJob": CronJob(o, list); break;
            case "Job": JobSection(o, list); break;
            case "Event": EventSection(o, list); break;
            case "ConfigMap": ConfigMap(o, list); break;
            case "PersistentVolumeClaim": Pvc(o, list); break;
            case "CustomResourceDefinition": Crd(o, list); break;
        }
        var cond = Conditions(o);
        if (cond is not null) list.Add(cond);
        return list;
    }

    private static DetailSection Metadata(KubeObject o, TimeSpan skew)
    {
        var s = new DetailSection("Metadata");
        s.Rows.Add(new("Name", o.Name));
        if (o.Namespace is not null) s.Rows.Add(new("Namespace", o.Namespace));
        s.Rows.Add(new("Created", o.CreationTimestamp is { } c ? $"{c.LocalDateTime:yyyy-MM-dd HH:mm:ss} ({Core.KubeTime.Age(c, skew)} ago)" : ""));
        s.Rows.Add(new("UID", o.Uid));
        if (o.OwnerReferences.Any()) s.Rows.Add(new("Controlled by", string.Join(", ", o.OwnerReferences.Select(r => $"{r.Kind}/{r.Name}"))));
        if (o.DeletionTimestamp is not null) s.Rows.Add(new("Deleting since", o.DeletionTimestamp.Value.LocalDateTime.ToString("u")));
        s.Chips.AddRange(o.Labels.Select(kv => $"{kv.Key}={kv.Value}"));
        return s;
    }

    public static IReadOnlyList<string> Annotations(KubeObject o) =>
        [.. o.Annotations.Where(kv => kv.Key != "kubectl.kubernetes.io/last-applied-configuration").Select(kv => $"{kv.Key}={Trim(kv.Value)}")];

    private static string Trim(string v) => v.Length > 80 ? v[..80] + "…" : v;

    private static void Pod(KubeObject o, List<DetailSection> list)
    {
        var pv = new PodView(o);
        var s = new DetailSection("Status");
        s.Rows.Add(new("Status", pv.Status)); s.Rows.Add(new("Ready", pv.Ready)); s.Rows.Add(new("Restarts", pv.Restarts.ToString()));
        s.Rows.Add(new("Node", pv.NodeName ?? "")); s.Rows.Add(new("Pod IP", pv.PodIp ?? "")); s.Rows.Add(new("QoS class", pv.QosClass ?? ""));
        list.Add(s);
        var t = new DetailSection("Containers")
        {
            Table = new DetailTable(["Name", "Image", "State", "Ready", "Restarts", "Ports", "CPU req/lim", "Mem req/lim"],
                pv.Containers.Select(c => (IReadOnlyList<string>)[(c.IsInit ? "init: " : "") + c.Name, c.Image, c.Reason is null ? c.State : $"{c.State} ({c.Reason})", c.Ready ? "yes" : "no", c.Restarts.ToString(),
                    string.Join(",", c.Ports), $"{c.CpuRequest ?? "-"}/{c.CpuLimit ?? "-"}", $"{c.MemoryRequest ?? "-"}/{c.MemoryLimit ?? "-"}"]).ToList()),
        };
        list.Add(t);
        // Environment: names with value sources; secret references stay references.
        var env = new List<IReadOnlyList<string>>();
        foreach (var c in (o.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>())
            foreach (var e in (c["env"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var name = KubeObject.Str(e, "name") ?? "";
                string value;
                if (e["valueFrom"] is JsonObject vf)
                {
                    if (vf["secretKeyRef"] is JsonObject sk) value = $"secret {KubeObject.Str(sk, "name")}:{KubeObject.Str(sk, "key")}";
                    else if (vf["configMapKeyRef"] is JsonObject ck) value = $"configmap {KubeObject.Str(ck, "name")}:{KubeObject.Str(ck, "key")}";
                    else if (vf["fieldRef"] is JsonObject fr) value = $"field {KubeObject.Str(fr, "fieldPath")}";
                    else value = "(reference)";
                }
                else value = KubeObject.Str(e, "value") ?? "";
                env.Add([KubeObject.Str(c, "name") ?? "", name, value]);
            }
        if (env.Count > 0) list.Add(new DetailSection("Environment") { Table = new DetailTable(["Container", "Name", "Value"], env) });
        var probes = new List<IReadOnlyList<string>>();
        foreach (var c in (o.At("spec", "containers") as JsonArray ?? []).OfType<JsonObject>())
            foreach (var p in new[] { "livenessProbe", "readinessProbe", "startupProbe" })
                if (c[p] is JsonObject pr)
                    probes.Add([KubeObject.Str(c, "name") ?? "", p, pr["httpGet"] is JsonObject h ? $"GET {KubeObject.Str(h, "path")}:{h["port"]}" : pr["tcpSocket"] is JsonObject tcp ? $"tcp :{tcp["port"]}" : pr["exec"] is not null ? "exec" : ""]);
        if (probes.Count > 0) list.Add(new DetailSection("Probes") { Table = new DetailTable(["Container", "Probe", "Check"], probes) });
    }

    private static void Workload(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("Status");
        s.Rows.Add(new("Replicas", $"{o.IntAt("status", "readyReplicas") ?? 0} ready / {o.IntAt("spec", "replicas") ?? (o.Kind == "DaemonSet" ? o.IntAt("status", "desiredNumberScheduled") ?? 0 : 1)} desired"));
        if (o.Kind == "Deployment")
        {
            var dv = new DeploymentView(o);
            s.Rows.Add(new("Up-to-date", dv.UpToDate.ToString())); s.Rows.Add(new("Available", dv.Available.ToString())); s.Rows.Add(new("Strategy", dv.Strategy));
            if (o.At("spec", "paused")?.ToString() == "True" || o.At("spec", "paused")?.ToString() == "true") s.Rows.Add(new("Paused", "yes"));
        }
        var sel = KubeObject.StringMap(o.At("spec", "selector", "matchLabels"));
        if (sel.Count > 0) s.Rows.Add(new("Selector", string.Join(",", sel.Select(kv => $"{kv.Key}={kv.Value}"))));
        list.Add(s);
        var containers = (o.At("spec", "template", "spec", "containers") as JsonArray ?? []).OfType<JsonObject>().Select(c =>
            (IReadOnlyList<string>)[KubeObject.Str(c, "name") ?? "", KubeObject.Str(c, "image") ?? "",
                $"{c["resources"]?["requests"]?["cpu"] ?? "-"}/{c["resources"]?["limits"]?["cpu"] ?? "-"}", $"{c["resources"]?["requests"]?["memory"] ?? "-"}/{c["resources"]?["limits"]?["memory"] ?? "-"}"]).ToList();
        if (containers.Count > 0) list.Add(new DetailSection("Containers") { Table = new DetailTable(["Name", "Image", "CPU req/lim", "Mem req/lim"], containers) });
    }

    private static void Service(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("Service");
        s.Rows.Add(new("Type", o.StringAt("spec", "type") ?? "ClusterIP")); s.Rows.Add(new("Cluster IP", o.StringAt("spec", "clusterIP") ?? ""));
        var sel = KubeObject.StringMap(o.At("spec", "selector"));
        s.Rows.Add(new("Selector", string.Join(",", sel.Select(kv => $"{kv.Key}={kv.Value}"))));
        var lb = (o.At("status", "loadBalancer", "ingress") as JsonArray)?.OfType<JsonObject>().Select(i => KubeObject.Str(i, "ip") ?? KubeObject.Str(i, "hostname") ?? "").ToList();
        if (lb is { Count: > 0 }) s.Rows.Add(new("Load balancer", string.Join(", ", lb)));
        list.Add(s);
        var ports = (o.At("spec", "ports") as JsonArray ?? []).OfType<JsonObject>().Select(p =>
            (IReadOnlyList<string>)[KubeObject.Str(p, "name") ?? "", p["port"]?.ToString() ?? "", p["targetPort"]?.ToString() ?? "", KubeObject.Str(p, "protocol") ?? "TCP", p["nodePort"]?.ToString() ?? ""]).ToList();
        list.Add(new DetailSection("Ports") { Table = new DetailTable(["Name", "Port", "Target port", "Protocol", "Node port"], ports) });
    }

    private static void Node(KubeObject o, List<DetailSection> list)
    {
        var nv = new NodeView(o);
        var s = new DetailSection("Node");
        s.Rows.Add(new("Status", nv.Status)); s.Rows.Add(new("Roles", nv.Roles)); s.Rows.Add(new("Kubelet", nv.Version ?? ""));
        s.Rows.Add(new("OS image", o.StringAt("status", "nodeInfo", "osImage") ?? ""));
        s.Rows.Add(new("Capacity", $"cpu {o.StringAt("status", "capacity", "cpu")}, memory {o.StringAt("status", "capacity", "memory")}, pods {o.StringAt("status", "capacity", "pods")}"));
        s.Rows.Add(new("Allocatable", $"cpu {o.StringAt("status", "allocatable", "cpu")}, memory {o.StringAt("status", "allocatable", "memory")}, pods {o.StringAt("status", "allocatable", "pods")}"));
        list.Add(s);
        var taints = (o.At("spec", "taints") as JsonArray ?? []).OfType<JsonObject>().Select(t => (IReadOnlyList<string>)[KubeObject.Str(t, "key") ?? "", KubeObject.Str(t, "value") ?? "", KubeObject.Str(t, "effect") ?? ""]).ToList();
        if (taints.Count > 0) list.Add(new DetailSection("Taints") { Table = new DetailTable(["Key", "Value", "Effect"], taints) });
    }

    private static void CronJob(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("CronJob");
        s.Rows.Add(new("Schedule", o.StringAt("spec", "schedule") ?? "")); s.Rows.Add(new("Suspended", o.At("spec", "suspend")?.ToString() ?? "false"));
        s.Rows.Add(new("Last schedule", o.StringAt("status", "lastScheduleTime") ?? "never"));
        list.Add(s);
    }

    private static void JobSection(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("Job");
        s.Rows.Add(new("Completions", $"{o.IntAt("status", "succeeded") ?? 0}/{o.IntAt("spec", "completions") ?? 1}"));
        s.Rows.Add(new("Active", (o.IntAt("status", "active") ?? 0).ToString())); s.Rows.Add(new("Failed", (o.IntAt("status", "failed") ?? 0).ToString()));
        list.Add(s);
    }

    private static void EventSection(KubeObject o, List<DetailSection> list)
    {
        var ev = new EventView(o);
        var s = new DetailSection("Event");
        s.Rows.Add(new("Type", ev.Type)); s.Rows.Add(new("Reason", ev.Reason)); s.Rows.Add(new("Object", $"{ev.InvolvedKind}/{ev.InvolvedName}")); s.Rows.Add(new("Message", ev.Message));
        list.Add(s);
    }

    private static void ConfigMap(KubeObject o, List<DetailSection> list)
    {
        var data = (o.At("data") as JsonObject)?.Select(kv => (IReadOnlyList<string>)[kv.Key, kv.Value?.ToString() ?? ""]).ToList() ?? [];
        list.Add(new DetailSection("Data") { Table = new DetailTable(["Key", "Value"], data) });
    }

    private static void Pvc(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("Claim");
        s.Rows.Add(new("Status", o.StringAt("status", "phase") ?? "")); s.Rows.Add(new("Volume", o.StringAt("spec", "volumeName") ?? ""));
        s.Rows.Add(new("Capacity", o.StringAt("status", "capacity", "storage") ?? "")); s.Rows.Add(new("Storage class", o.StringAt("spec", "storageClassName") ?? ""));
        list.Add(s);
    }

    /// <summary>CRD browser: names, versions, and the OpenAPI v3 schema of the storage version flattened to a path/type table.</summary>
    private static void Crd(KubeObject o, List<DetailSection> list)
    {
        var s = new DetailSection("Definition");
        s.Rows.Add(new("Group", o.StringAt("spec", "group") ?? "")); s.Rows.Add(new("Kind", o.StringAt("spec", "names", "kind") ?? ""));
        s.Rows.Add(new("Plural", o.StringAt("spec", "names", "plural") ?? "")); s.Rows.Add(new("Scope", o.StringAt("spec", "scope") ?? ""));
        list.Add(s);
        var versions = (o.At("spec", "versions") as JsonArray ?? []).OfType<JsonObject>().ToList();
        list.Add(new DetailSection("Versions") { Table = new DetailTable(["Version", "Served", "Storage"],
            versions.Select(v => (IReadOnlyList<string>)[KubeObject.Str(v, "name") ?? "", v["served"]?.ToString() ?? "", v["storage"]?.ToString() ?? ""]).ToList()) });
        var storage = versions.FirstOrDefault(v => v["storage"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true) ?? versions.FirstOrDefault();
        if (storage?["schema"]?["openAPIV3Schema"] is JsonObject schema)
        {
            var rows = new List<IReadOnlyList<string>>();
            Flatten(schema, "", 0, rows);
            list.Add(new DetailSection($"Schema ({KubeObject.Str(storage, "name")})") { Table = new DetailTable(["Path", "Type", "Description"], rows) });
        }
    }

    public static void Flatten(JsonObject schema, string path, int depth, List<IReadOnlyList<string>> rows)
    {
        if (depth > 6 || rows.Count > 400) return;
        if (schema["properties"] is JsonObject props)
            foreach (var (name, node) in props)
            {
                if (node is not JsonObject p) continue;
                var type = KubeObject.Str(p, "type") ?? (p["x-kubernetes-preserve-unknown-fields"] is not null ? "object (free-form)" : "");
                var desc = KubeObject.Str(p, "description") ?? "";
                if (desc.Length > 90) desc = desc[..90] + "…";
                var full = path.Length == 0 ? name : $"{path}.{name}";
                rows.Add([new string(' ', depth * 2) + full, type, desc]);
                Flatten(p, full, depth + 1, rows);
            }
        if (schema["items"] is JsonObject items) Flatten(items, path + "[]", depth, rows);
    }

    private static DetailSection? Conditions(KubeObject o)
    {
        var rows = (o.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().Select(c =>
            (IReadOnlyList<string>)[KubeObject.Str(c, "type") ?? "", KubeObject.Str(c, "status") ?? "", KubeObject.Str(c, "reason") ?? "", KubeObject.Str(c, "message") ?? ""]).ToList();
        return rows.Count == 0 ? null : new DetailSection("Conditions") { Table = new DetailTable(["Type", "Status", "Reason", "Message"], rows) };
    }
}
