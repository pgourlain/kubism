using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Core.Network;

/// <summary>
/// Where traffic can come from, judged from the cluster's own objects. <see cref="Outside"/> is reachable from outside the cluster
/// (not necessarily from the Internet: an internal cloud load balancer is outside the cluster too). <see cref="Unknown"/>: the ingress controller could not be found.
/// </summary>
public enum ExposureLevel { InCluster = 0, Unknown = 1, Outside = 2 }

public enum NetworkNodeKind { Exposure = 0, Ingress = 1, Service = 2, Workload = 3, Node = 4 }

public sealed record NetworkProblem(string Code, string Description, FindingSeverity Severity);

/// <summary>One node. <see cref="Namespace"/> is null for something outside the cluster. <see cref="Lines"/> are the details shown when the node is selected.</summary>
public sealed record NetworkNode(string Id, NetworkNodeKind Kind, string Label, string? Namespace, string Detail, ExposureLevel Exposure,
    IReadOnlyList<NetworkProblem> Problems, IReadOnlyList<string> Lines, KubeObject? Source = null, bool Missing = false, string? Note = null, IReadOnlyList<string>? Filters = null)
{
    /// <summary>For an Ingress: what it matches on the way in, one line per host ("shop.example.com: /, /api", "default backend"). Drawn inside the box.</summary>
    public IReadOnlyList<string> FilterLines { get; init; } = Filters ?? [];
    public bool HasProblem => Problems.Count > 0;
    public FindingSeverity? Worst => Problems.Count == 0 ? null : Problems.Max(p => p.Severity);
    public string KindText => Kind switch { NetworkNodeKind.Exposure => "Exposure", NetworkNodeKind.Ingress => "Ingress", NetworkNodeKind.Service => "Service", NetworkNodeKind.Node => "Node", _ => "Workload" };
}

/// <param name="Label">The full text of the hop (paths, port mapping), for tooltips and the MCP tool.</param>
/// <param name="Problem">The ports on this hop do not line up (missing service or port, unresolved target port).</param>
public sealed record NetworkEdge(string From, string To, string Label, bool Problem)
{
    /// <summary>The ports at the entry of <see cref="To"/>: the controller's ports into an Ingress, the Service ports into a Service, the container ports into a workload ("?" when unresolved).</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];
}

/// <summary>What is known about a TLS secret named by an Ingress. Only existence and the public certificate's expiry are read; nothing of the secret is kept.</summary>
public sealed record TlsSecretStatus(bool? Exists, DateTimeOffset? NotAfter, string? Detail = null);

/// <summary>One URL an Ingress serves: host + path to a backend Service port. <see cref="Host"/> is "*" for a rule with no host (any host).</summary>
public sealed record NetworkRoute(string IngressId, string Namespace, string Ingress, string? Class, string Host, string Path, string PathType, string Service, string ServicePort,
    string ServiceId, bool DefaultBackend, bool TlsCovered, bool BackendMissing);

/// <summary>A route joined with what serves it, for the "which URL goes through which Ingress" table.</summary>
public sealed record NetworkRouteRow(NetworkRoute Route, string Url, ExposureLevel Exposure, string Via, string Backend, string Target, string Problems, FindingSeverity? Worst)
{
    public string Ingress => $"{Route.Namespace}/{Route.Ingress}";
    public string IngressId => Route.IngressId;
    public string ExposureText => Exposure switch { ExposureLevel.Outside => "outside the cluster", ExposureLevel.Unknown => "unknown", _ => "in-cluster only" };
}

public enum ExposureFilter { Any, OutsideOnly, InClusterOnly }

/// <summary>Which part of the graph to draw.</summary>
public sealed record NetworkFilter
{
    /// <summary>Namespaces to show (null: all). The ingress controller and other exposure nodes of another namespace stay as context, so a path is never cut.</summary>
    public IReadOnlySet<string>? Namespaces { get; init; }
    public ExposureFilter Exposure { get; init; } = ExposureFilter.Any;
    public bool ProblemsOnly { get; init; }
    /// <summary>Host, path, service, workload or namespace.</summary>
    public string? Search { get; init; }
    /// <summary>Also draw Services nothing routes to (ClusterIP only). They are many and rarely what one is looking for, so off by default.</summary>
    public bool IncludeInternalServices { get; init; }
    /// <summary>A fifth column: the nodes the pods run on, one box per node shared by every workload that has a pod there. Off by default: it adds boxes and arrows.</summary>
    public bool ShowNodes { get; init; }
    public int MaxChains { get; init; } = 120;
}

public sealed record PlacedNode(NetworkNode Node, int Column, int Row, bool IsContext)
{
    public string Id => Node.Id;
}

/// <summary>A filtered, laid-out view: four columns Exposure → Ingress → Service → Workload.</summary>
public sealed class NetworkView
{
    public IReadOnlyList<PlacedNode> Nodes { get; init; } = [];
    public IReadOnlyList<NetworkEdge> Edges { get; init; } = [];
    public int MaxRows { get; init; }
    /// <summary>4 (Exposure, Ingresses, Services, Workloads), or 5 with the nodes column.</summary>
    public int Columns { get; init; } = 4;
    public bool Truncated { get; init; }
    public int Chains { get; init; }
    /// <summary>Every URL the shown Ingresses serve, sorted by host then path.</summary>
    public IReadOnlyList<NetworkRouteRow> Routes { get; init; } = [];
    public int OutsideChains { get; init; }
    public int ProblemChains { get; init; }
    public string? Warning { get; init; }
}
