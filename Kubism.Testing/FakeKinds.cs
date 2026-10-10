using System.Text.Json.Nodes;
using Kubism.Core;
using Kubism.Core.Api;

namespace Kubism.Testing;

public sealed record FakeColumn(string Name, string Type, int Priority, Func<JsonObject, string?> Cell);

public sealed record FakeKind(string Group, string Version, string Resource, string Kind, bool Namespaced, string[] ShortNames,
    string[] Verbs, string[] Subresources, FakeColumn[] Columns, string[]? Categories = null)
{
    public Gvr Gvr => new(Group, Version, Resource);
}

public static class FakeKinds
{
    private static readonly string[] Std = ["get", "list", "watch", "create", "update", "patch", "delete"];
    private static readonly string[] StdColl = ["get", "list", "watch", "create", "update", "patch", "delete", "deletecollection"];

    public static string? S(JsonObject o, params string[] path)
    {
        JsonNode? n = o;
        foreach (var p in path) { if (n is JsonObject j) n = j[p]; else return null; }
        return n is JsonValue v ? v.ToString() : null;
    }

    public static string Age(JsonObject o)
    {
        var ts = S(o, "metadata", "creationTimestamp");
        if (ts is null || !DateTimeOffset.TryParse(ts, out var t)) return "<unknown>";
        return KubeTime.Age(DateTimeOffset.UtcNow - t);
    }

    private static FakeColumn Name => new("Name", "string", 0, o => S(o, "metadata", "name"));
    private static FakeColumn AgeCol => new("Age", "string", 0, Age);

    private static string PodStatus(JsonObject o)
    {
        if (S(o, "metadata", "deletionTimestamp") is not null) return "Terminating";
        foreach (var cs in (o["status"]?["containerStatuses"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var reason = S(cs, "state", "waiting", "reason");
            if (reason is not null) return reason;
            var term = S(cs, "state", "terminated", "reason");
            if (term is not null) return term;
        }
        return S(o, "status", "phase") ?? "Unknown";
    }

    private static string PodReady(JsonObject o)
    {
        var cs = (o["status"]?["containerStatuses"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var total = (o["spec"]?["containers"] as JsonArray)?.Count ?? cs.Count;
        return $"{cs.Count(c => c["ready"]?.GetValue<bool>() == true)}/{total}";
    }

    private static string PodRestarts(JsonObject o) =>
        (o["status"]?["containerStatuses"] as JsonArray ?? []).OfType<JsonObject>().Sum(c => c["restartCount"]?.GetValue<int>() ?? 0).ToString();

    public static readonly IReadOnlyList<FakeKind> All =
    [
        new("", "v1", "pods", "Pod", true, ["po"], StdColl, ["log", "exec", "portforward", "eviction", "status", "attach"],
            [Name, new("Ready", "string", 0, PodReady), new("Status", "string", 0, PodStatus), new("Restarts", "string", 0, PodRestarts), AgeCol,
             new("IP", "string", 1, o => S(o, "status", "podIP")), new("Node", "string", 1, o => S(o, "spec", "nodeName"))], ["all"]),
        new("", "v1", "nodes", "Node", false, ["no"], StdColl, ["status"],
            [Name, new("Status", "string", 0, _ => "Ready"), new("Roles", "string", 0, o => S(o, "metadata", "labels", "node-role.kubernetes.io/control-plane") is not null ? "control-plane" : "<none>"),
             AgeCol, new("Version", "string", 0, o => S(o, "status", "nodeInfo", "kubeletVersion"))]),
        new("", "v1", "namespaces", "Namespace", false, ["ns"], StdColl, ["status"], [Name, new("Status", "string", 0, o => S(o, "status", "phase") ?? "Active"), AgeCol]),
        new("", "v1", "services", "Service", true, ["svc"], Std, ["status"],
            [Name, new("Type", "string", 0, o => S(o, "spec", "type")), new("Cluster-IP", "string", 0, o => S(o, "spec", "clusterIP")), AgeCol], ["all"]),
        new("", "v1", "configmaps", "ConfigMap", true, ["cm"], StdColl, [], [Name, new("Data", "integer", 0, o => ((o["data"] as JsonObject)?.Count ?? 0).ToString()), AgeCol]),
        new("", "v1", "secrets", "Secret", true, [], StdColl, [],
            [Name, new("Type", "string", 0, o => S(o, "type")), new("Data", "integer", 0, o => ((o["data"] as JsonObject)?.Count ?? 0).ToString()), AgeCol]),
        new("", "v1", "events", "Event", true, ["ev"], StdColl, [],
            [new("Last Seen", "string", 0, Age), new("Type", "string", 0, o => S(o, "type")), new("Reason", "string", 0, o => S(o, "reason")),
             new("Object", "string", 0, o => $"{S(o, "involvedObject", "kind")?.ToLowerInvariant()}/{S(o, "involvedObject", "name")}"), new("Message", "string", 0, o => S(o, "message"))]),
        new("", "v1", "persistentvolumeclaims", "PersistentVolumeClaim", true, ["pvc"], StdColl, ["status"], [Name, new("Status", "string", 0, o => S(o, "status", "phase")), AgeCol]),
        new("", "v1", "serviceaccounts", "ServiceAccount", true, ["sa"], StdColl, [], [Name, AgeCol]),
        new("", "v1", "endpoints", "Endpoints", true, ["ep"], StdColl, [], [Name, AgeCol]),
        new("apps", "v1", "deployments", "Deployment", true, ["deploy"], StdColl, ["scale", "status"],
            [Name, new("Ready", "string", 0, o => $"{S(o, "status", "readyReplicas") ?? "0"}/{S(o, "spec", "replicas") ?? "1"}"),
             new("Up-to-date", "integer", 0, o => S(o, "status", "updatedReplicas") ?? "0"), new("Available", "integer", 0, o => S(o, "status", "availableReplicas") ?? "0"), AgeCol], ["all"]),
        new("apps", "v1", "replicasets", "ReplicaSet", true, ["rs"], StdColl, ["scale", "status"],
            [Name, new("Desired", "integer", 0, o => S(o, "spec", "replicas")), new("Current", "integer", 0, o => S(o, "status", "replicas")), AgeCol], ["all"]),
        new("apps", "v1", "statefulsets", "StatefulSet", true, ["sts"], StdColl, ["scale", "status"], [Name, new("Ready", "string", 0, o => $"{S(o, "status", "readyReplicas") ?? "0"}/{S(o, "spec", "replicas") ?? "1"}"), AgeCol], ["all"]),
        new("apps", "v1", "daemonsets", "DaemonSet", true, ["ds"], StdColl, ["status"], [Name, new("Desired", "integer", 0, o => S(o, "status", "desiredNumberScheduled")), AgeCol], ["all"]),
        new("batch", "v1", "jobs", "Job", true, [], StdColl, ["status"], [Name, new("Status", "string", 0, o => S(o, "status", "succeeded") is "1" ? "Complete" : "Running"), AgeCol], ["all"]),
        new("batch", "v1", "cronjobs", "CronJob", true, ["cj"], StdColl, ["status"],
            [Name, new("Schedule", "string", 0, o => S(o, "spec", "schedule")), new("Suspend", "boolean", 0, o => S(o, "spec", "suspend") ?? "false"), AgeCol], ["all"]),
        new("networking.k8s.io", "v1", "ingresses", "Ingress", true, ["ing"], StdColl, ["status"], [Name, AgeCol]),
        new("networking.k8s.io", "v1", "ingressclasses", "IngressClass", false, [], StdColl, [], [Name, new("Controller", "string", 0, o => S(o, "spec", "controller")), AgeCol]),
        new("discovery.k8s.io", "v1", "endpointslices", "EndpointSlice", true, [], StdColl, [], [Name, AgeCol]),
        new("policy", "v1", "poddisruptionbudgets", "PodDisruptionBudget", true, ["pdb"], StdColl, ["status"], [Name, AgeCol]),
        new("autoscaling", "v2", "horizontalpodautoscalers", "HorizontalPodAutoscaler", true, ["hpa"], StdColl, ["status"], [Name, AgeCol]),
        new("autoscaling.k8s.io", "v1", "verticalpodautoscalers", "VerticalPodAutoscaler", true, ["vpa"], StdColl, ["status"], [Name, AgeCol]),
        new("storage.k8s.io", "v1", "storageclasses", "StorageClass", false, ["sc"], StdColl, [], [Name, AgeCol]),
        new("rbac.authorization.k8s.io", "v1", "roles", "Role", true, [], StdColl, [], [Name, AgeCol]),
        new("rbac.authorization.k8s.io", "v1", "clusterroles", "ClusterRole", false, [], StdColl, [], [Name, AgeCol]),
        new("rbac.authorization.k8s.io", "v1", "rolebindings", "RoleBinding", true, [], StdColl, [], [Name, AgeCol]),
        new("rbac.authorization.k8s.io", "v1", "clusterrolebindings", "ClusterRoleBinding", false, [], StdColl, [], [Name, AgeCol]),
        new("apiextensions.k8s.io", "v1", "customresourcedefinitions", "CustomResourceDefinition", false, ["crd", "crds"], StdColl, ["status"],
            [Name, new("Created At", "date", 0, o => S(o, "metadata", "creationTimestamp"))]),
        new("cert-manager.io", "v1", "certificates", "Certificate", true, ["cert", "certs"], StdColl, ["status"],
            [Name, new("Ready", "string", 0, o => S(o, "status", "ready") ?? "False"), new("Secret", "string", 0, o => S(o, "spec", "secretName")),
             new("Issuer", "string", 1, o => S(o, "spec", "issuerRef", "name")), AgeCol]),
        new("argoproj.io", "v1alpha1", "applications", "Application", true, ["app", "apps"], StdColl, ["status"],
            [Name, new("Sync Status", "string", 0, o => S(o, "status", "sync", "status")), new("Health Status", "string", 0, o => S(o, "status", "health", "status")), AgeCol]),
        new("kustomize.toolkit.fluxcd.io", "v1", "kustomizations", "Kustomization", true, ["ks"], StdColl, ["status"], [Name, AgeCol]),
        new("helm.toolkit.fluxcd.io", "v2", "helmreleases", "HelmRelease", true, ["hr"], StdColl, ["status"], [Name, AgeCol]),
        new("source.toolkit.fluxcd.io", "v1", "gitrepositories", "GitRepository", true, ["gitrepo"], StdColl, ["status"], [Name, AgeCol]),
    ];

    public static FakeKind? Find(string group, string version, string resource) =>
        All.FirstOrDefault(k => k.Group == group && k.Version == version && k.Resource == resource);

    public static FakeKind? ByKind(string apiVersion, string kind)
    {
        var group = apiVersion.Contains('/') ? apiVersion[..apiVersion.IndexOf('/')] : "";
        return All.FirstOrDefault(k => k.Group == group && k.Kind == kind);
    }
}
