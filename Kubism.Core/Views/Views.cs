using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Views;

public sealed record ContainerInfo(string Name, string Image, bool IsInit, bool Ready, int Restarts, string State, string? Reason,
    string? CpuRequest, string? CpuLimit, string? MemoryRequest, string? MemoryLimit, IReadOnlyList<int> Ports);

/// <summary>Pod fields as kubectl computes them (printPod).</summary>
public sealed class PodView(KubeObject o)
{
    public KubeObject Object { get; } = o;
    public string Phase => Object.StringAt("status", "phase") ?? "Unknown";
    public string? NodeName => Object.StringAt("spec", "nodeName");
    public string? PodIp => Object.StringAt("status", "podIP");
    public string? QosClass => Object.StringAt("status", "qosClass");

    private IEnumerable<JsonObject> Statuses(string key) => (Object.At("status", key) as JsonArray ?? []).OfType<JsonObject>();

    public int ContainerCount => (Object.At("spec", "containers") as JsonArray)?.Count ?? 0;
    public int ReadyCount => Statuses("containerStatuses").Count(c => c["ready"]?.GetValue<bool>() == true);
    public string Ready => $"{ReadyCount}/{ContainerCount}";

    public int Restarts => Statuses("containerStatuses").Sum(c => c["restartCount"] is { } n ? Convert.ToInt32(n.ToString()) : 0);

    /// <summary>The STATUS column of <c>kubectl get pods</c>.</summary>
    public string Status
    {
        get
        {
            var reason = Phase;
            if (Object.StringAt("status", "reason") is { Length: > 0 } r) reason = r;
            var initializing = false;
            var inits = Statuses("initContainerStatuses").ToList();
            var initTotal = (Object.At("spec", "initContainers") as JsonArray)?.Count ?? inits.Count;
            for (var i = 0; i < inits.Count; i++)
            {
                var st = inits[i]["state"] as JsonObject;
                var term = st?["terminated"] as JsonObject;
                var wait = st?["waiting"] as JsonObject;
                if (term is not null && Int(term, "exitCode") == 0) continue;
                if (term is not null)
                {
                    var tr = KubeObject.Str(term, "reason");
                    if (string.IsNullOrEmpty(tr))
                        reason = Int(term, "signal") != 0 ? $"Init:Signal:{Int(term, "signal")}" : $"Init:ExitCode:{Int(term, "exitCode")}";
                    else reason = "Init:" + tr;
                    initializing = true;
                }
                else if (wait is not null && KubeObject.Str(wait, "reason") is { Length: > 0 } wr && wr != "PodInitializing")
                {
                    reason = "Init:" + wr; initializing = true;
                }
                else { reason = $"Init:{i}/{initTotal}"; initializing = true; }
                break;
            }
            if (!initializing)
            {
                var hasRunning = false;
                foreach (var cs in Statuses("containerStatuses").Reverse())
                {
                    var st = cs["state"] as JsonObject;
                    var wait = st?["waiting"] as JsonObject; var term = st?["terminated"] as JsonObject;
                    if (wait is not null && KubeObject.Str(wait, "reason") is { Length: > 0 } wr) reason = wr;
                    else if (term is not null && KubeObject.Str(term, "reason") is { Length: > 0 } tr) reason = tr;
                    else if (term is not null) reason = Int(term, "signal") != 0 ? $"Signal:{Int(term, "signal")}" : $"ExitCode:{Int(term, "exitCode")}";
                    else if (cs["ready"]?.GetValue<bool>() == true && st?["running"] is not null) hasRunning = true;
                }
                if (reason == "Completed" && hasRunning)
                    reason = ConditionTrue("Ready") ? "Running" : "NotReady";
            }
            if (Object.DeletionTimestamp is not null)
                reason = Object.StringAt("status", "reason") == "NodeLost" ? "Unknown" : "Terminating";
            return reason;
        }
    }

    private bool ConditionTrue(string type) =>
        (Object.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().Any(c => KubeObject.Str(c, "type") == type && KubeObject.Str(c, "status") == "True");

    private static int Int(JsonObject o, string key) => o[key] is { } n && int.TryParse(n.ToString(), out var i) ? i : 0;

    public IReadOnlyList<ContainerInfo> Containers
    {
        get
        {
            var list = new List<ContainerInfo>();
            void Add(string specKey, string statusKey, bool init)
            {
                var statuses = Statuses(statusKey).ToDictionary(s => KubeObject.Str(s, "name") ?? "", s => s);
                foreach (var c in (Object.At("spec", specKey) as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = KubeObject.Str(c, "name") ?? "";
                    statuses.TryGetValue(name, out var st);
                    var state = "Waiting"; string? reason = null;
                    if (st?["state"] is JsonObject s)
                    {
                        if (s["running"] is not null) state = "Running";
                        else if (s["terminated"] is JsonObject t) { state = "Terminated"; reason = KubeObject.Str(t, "reason"); }
                        else if (s["waiting"] is JsonObject w) { state = "Waiting"; reason = KubeObject.Str(w, "reason"); }
                    }
                    var res = c["resources"] as JsonObject;
                    string? R(string a, string b) => ((res?[a] as JsonObject)?[b])?.ToString();
                    list.Add(new ContainerInfo(name, KubeObject.Str(c, "image") ?? "", init, st?["ready"]?.GetValue<bool>() ?? false,
                        st?["restartCount"] is { } n ? Convert.ToInt32(n.ToString()) : 0, state, reason,
                        R("requests", "cpu"), R("limits", "cpu"), R("requests", "memory"), R("limits", "memory"),
                        (c["ports"] as JsonArray ?? []).OfType<JsonObject>().Select(p => Int(p, "containerPort")).Where(p => p > 0).ToList()));
                }
            }
            Add("initContainers", "initContainerStatuses", true);
            Add("containers", "containerStatuses", false);
            Add("ephemeralContainers", "ephemeralContainerStatuses", false);
            return list;
        }
    }

    public bool IsProblem(DateTimeOffset now)
    {
        var s = Status;
        if (s is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or "Error" or "OOMKilled" or "Evicted" or "CreateContainerConfigError") return true;
        return s == "Pending" && Object.CreationTimestamp is { } c && now - c > TimeSpan.FromMinutes(5);
    }
}

public sealed class NodeView(KubeObject o)
{
    public KubeObject Object { get; } = o;
    public string Status
    {
        get
        {
            var ready = (Object.At("status", "conditions") as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(c => KubeObject.Str(c, "type") == "Ready");
            var s = ready is null ? "Unknown" : KubeObject.Str(ready, "status") == "True" ? "Ready" : "NotReady";
            return Object.At("spec", "unschedulable")?.GetValue<bool>() == true ? s + ",SchedulingDisabled" : s;
        }
    }
    public bool IsReady => Status.StartsWith("Ready");
    public string Roles
    {
        get
        {
            var roles = Object.Labels.Keys.Where(k => k.StartsWith("node-role.kubernetes.io/")).Select(k => k["node-role.kubernetes.io/".Length..]).OrderBy(x => x).ToList();
            return roles.Count == 0 ? "<none>" : string.Join(",", roles);
        }
    }
    public string? Version => Object.StringAt("status", "nodeInfo", "kubeletVersion");
    public double CpuCapacity => Quantities.ParseCpu(Object.StringAt("status", "capacity", "cpu"));
    public double MemoryCapacity => Quantities.ParseBytes(Object.StringAt("status", "capacity", "memory"));
    public double CpuAllocatable => Quantities.ParseCpu(Object.StringAt("status", "allocatable", "cpu"));
    public double MemoryAllocatable => Quantities.ParseBytes(Object.StringAt("status", "allocatable", "memory"));
}

public sealed class DeploymentView(KubeObject o)
{
    public KubeObject Object { get; } = o;
    public int Desired => Object.IntAt("spec", "replicas") ?? 1;
    public int Ready => Object.IntAt("status", "readyReplicas") ?? 0;
    public int UpToDate => Object.IntAt("status", "updatedReplicas") ?? 0;
    public int Available => Object.IntAt("status", "availableReplicas") ?? 0;
    public string Strategy => Object.StringAt("spec", "strategy", "type") ?? "RollingUpdate";
}

public sealed class EventView(KubeObject o)
{
    public KubeObject Object { get; } = o;
    public string Type => Object.StringAt("type") ?? "Normal";
    public bool IsWarning => Type == "Warning";
    public string Reason => Object.StringAt("reason") ?? "";
    public string Message => Object.StringAt("message") ?? "";
    public string InvolvedKind => Object.StringAt("involvedObject", "kind") ?? "";
    public string InvolvedName => Object.StringAt("involvedObject", "name") ?? "";
    public DateTimeOffset? LastSeen => KubeObject.Time(Object.Json, "lastTimestamp") ?? KubeObject.Time(Object.Json, "eventTime") ?? Object.CreationTimestamp;
}
