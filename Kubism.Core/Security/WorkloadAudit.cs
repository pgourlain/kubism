using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>
/// Checks the pod spec of a workload (or of a bare pod) against the usual hardening advice: privileged containers, host namespaces and paths,
/// root, privilege escalation, dangerous capabilities, unpinned images, the default service account. Controllers are audited through their
/// template; pods a controller owns are skipped, so one problem is reported once.
/// </summary>
public static class WorkloadAudit
{
    public static readonly HashSet<string> Kinds = ["Pod", "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "Job", "CronJob"];
    private static readonly HashSet<string> SensitiveHostPaths = ["/", "/etc", "/proc", "/sys", "/var/run/docker.sock", "/run/containerd/containerd.sock", "/var/run/crio/crio.sock", "/var/lib/kubelet", "/root"];
    private static readonly HashSet<string> DangerousCaps = ["SYS_ADMIN", "NET_ADMIN", "SYS_PTRACE", "SYS_MODULE", "DAC_READ_SEARCH", "ALL"];

    /// <summary>True for objects worth auditing: controllers, and pods nothing controls.</summary>
    public static bool ShouldAudit(KubeObject o) =>
        Kinds.Contains(o.Kind) && o.Kind != "ReplicaSet" && !(o.Kind == "Pod" && o.OwnerReferences.Any(r => r.Controller)) && !(o.Kind == "Job" && o.OwnerReferences.Any(r => r.Controller));

    public static IReadOnlyList<Finding> Check(KubeObject o)
    {
        var findings = new List<Finding>();
        if (PodSpecOf(o) is not { } spec) return findings;
        void Add(FindingSeverity sev, string rule, string title, string detail) =>
            findings.Add(new Finding(sev, rule, title, detail, o.ToString(), o.Kind, o.Namespace, o.Name));

        var podSc = spec["securityContext"] as JsonObject;
        foreach (var flag in new[] { ("hostNetwork", "Uses the host network"), ("hostPID", "Shares the host PID namespace"), ("hostIPC", "Shares the host IPC namespace") })
            if (spec[flag.Item1]?.ToString() == "True" || spec[flag.Item1]?.ToString() == "true")
                Add(FindingSeverity.High, flag.Item1.ToLowerInvariant(), flag.Item2, $"spec.{flag.Item1} is true: the pod can see the node's traffic or processes.");
        foreach (var v in spec["volumes"] as JsonArray ?? [])
            if (v?["hostPath"]?["path"]?.ToString() is { } path)
            {
                var sensitive = SensitiveHostPaths.Contains(path.TrimEnd('/') is { Length: > 0 } t ? t : "/") || path.EndsWith(".sock");
                Add(sensitive ? FindingSeverity.High : FindingSeverity.Medium, "hostpath", $"Mounts host path {path}", sensitive ? "Gives access to the node's sensitive files or container runtime socket." : "The pod depends on the node's file system and can read or write it.");
            }
        if (IsTrue(spec["automountServiceAccountToken"]) || (spec["automountServiceAccountToken"] is null && ServiceAccountOf(spec) == "default"))
            if (ServiceAccountOf(spec) == "default")
                Add(FindingSeverity.Low, "default-serviceaccount", "Uses the default service account", "Its token is mounted in the pod; give the workload its own account, or set automountServiceAccountToken: false.");

        foreach (var (c, init) in Containers(spec))
        {
            var name = c["name"]?.ToString() ?? "?";
            var sc = c["securityContext"] as JsonObject;
            var where = init ? $"Init container {name}" : $"Container {name}";
            if (IsTrue(sc?["privileged"])) Add(FindingSeverity.High, "privileged", $"{where} is privileged", "Privileged containers have almost the same access as root on the node.");
            var runAsUser = Long(sc?["runAsUser"]) ?? Long(podSc?["runAsUser"]);
            var nonRoot = IsTrue(sc?["runAsNonRoot"]) || IsTrue(podSc?["runAsNonRoot"]);
            if (runAsUser == 0) Add(FindingSeverity.Medium, "run-as-root", $"{where} runs as root", "runAsUser is 0.");
            else if (!nonRoot && runAsUser is null) Add(FindingSeverity.Low, "may-run-as-root", $"{where} may run as root", "Neither runAsNonRoot nor runAsUser is set, so the image decides.");
            if (!IsFalse(sc?["allowPrivilegeEscalation"]) && !IsTrue(sc?["privileged"])) Add(FindingSeverity.Low, "privilege-escalation", $"{where} can gain privileges", "allowPrivilegeEscalation is not set to false.");
            foreach (var cap in (sc?["capabilities"]?["add"] as JsonArray ?? []).Select(x => x?.ToString()?.ToUpperInvariant().Replace("CAP_", "")).Where(x => x is not null && DangerousCaps.Contains(x)))
                Add(FindingSeverity.High, "capability", $"{where} adds capability {cap}", "This capability widens what the container can do to the node or to other containers.");
            if (c["image"]?.ToString() is { } image && IsUnpinned(image)) Add(FindingSeverity.Low, "latest-tag", $"{where} uses an unpinned image", $"{image}: pin a version or a digest so a rollout is reproducible.");
        }
        return findings;
    }

    private static bool IsUnpinned(string image)
    {
        if (image.Contains('@')) return false;
        var last = image[(image.LastIndexOf('/') + 1)..];
        return !last.Contains(':') || last.EndsWith(":latest", StringComparison.Ordinal);
    }

    private static string ServiceAccountOf(JsonObject spec) => spec["serviceAccountName"]?.ToString() ?? spec["serviceAccount"]?.ToString() ?? "default";
    private static bool IsTrue(JsonNode? n) => n is not null && n.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
    private static bool IsFalse(JsonNode? n) => n is not null && n.ToString().Equals("false", StringComparison.OrdinalIgnoreCase);
    private static long? Long(JsonNode? n) => n is not null && long.TryParse(n.ToString(), out var l) ? l : null;

    private static IEnumerable<(JsonObject c, bool init)> Containers(JsonObject spec)
    {
        foreach (var c in spec["initContainers"] as JsonArray ?? []) if (c is JsonObject co) yield return (co, true);
        foreach (var c in spec["containers"] as JsonArray ?? []) if (c is JsonObject co) yield return (co, false);
    }

    private static JsonObject? PodSpecOf(KubeObject o) => o.Kind switch
    {
        "Pod" => o.At("spec") as JsonObject,
        "CronJob" => o.At("spec", "jobTemplate", "spec", "template", "spec") as JsonObject,
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "Job" => o.At("spec", "template", "spec") as JsonObject,
        _ => null,
    };
}
