namespace Kubism.Core.Api;

/// <summary>Group/version/resource triple (plural resource name, e.g. "pods").</summary>
public readonly record struct Gvr(string Group, string Version, string Resource)
{
    public string ApiVersion => Group.Length == 0 ? Version : $"{Group}/{Version}";

    public string BasePath => Group.Length == 0 ? $"/api/{Version}" : $"/apis/{Group}/{Version}";

    /// <summary>Collection path, optionally scoped to a namespace.</summary>
    public string CollectionPath(string? ns = null) =>
        string.IsNullOrEmpty(ns) ? $"{BasePath}/{Resource}" : $"{BasePath}/namespaces/{Uri.EscapeDataString(ns)}/{Resource}";

    public string ObjectPath(string? ns, string name, string? subresource = null)
    {
        var p = $"{CollectionPath(ns)}/{Uri.EscapeDataString(name)}";
        return subresource is null ? p : $"{p}/{subresource}";
    }

    public override string ToString() => Group.Length == 0 ? $"{Resource}.{Version}" : $"{Resource}.{Version}.{Group}";

    public static readonly Gvr Pods = new("", "v1", "pods");
    public static readonly Gvr Nodes = new("", "v1", "nodes");
    public static readonly Gvr Namespaces = new("", "v1", "namespaces");
    public static readonly Gvr Events = new("", "v1", "events");
    public static readonly Gvr Secrets = new("", "v1", "secrets");
    public static readonly Gvr ConfigMaps = new("", "v1", "configmaps");
    public static readonly Gvr Services = new("", "v1", "services");
    public static readonly Gvr Deployments = new("apps", "v1", "deployments");
    public static readonly Gvr ReplicaSets = new("apps", "v1", "replicasets");
    public static readonly Gvr StatefulSets = new("apps", "v1", "statefulsets");
    public static readonly Gvr DaemonSets = new("apps", "v1", "daemonsets");
    public static readonly Gvr Jobs = new("batch", "v1", "jobs");
    public static readonly Gvr CronJobs = new("batch", "v1", "cronjobs");
    public static readonly Gvr Crds = new("apiextensions.k8s.io", "v1", "customresourcedefinitions");
    public static readonly Gvr PodMetrics = new("metrics.k8s.io", "v1beta1", "pods");
    public static readonly Gvr NodeMetrics = new("metrics.k8s.io", "v1beta1", "nodes");
}
