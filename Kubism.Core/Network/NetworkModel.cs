using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Core.Network;

/// <summary>
/// Where traffic can come from, judged from the cluster's own objects. <see cref="Outside"/> is reachable from outside the cluster
/// (not necessarily from the Internet: an internal cloud load balancer is outside the cluster too). <see cref="Unknown"/>: the ingress controller could not be found.
/// </summary>
public enum ExposureLevel { InCluster = 0, Unknown = 1, Outside = 2 }

public enum NetworkNodeKind { Exposure = 0, Ingress = 1, Service = 2, Workload = 3 }

public sealed record NetworkProblem(string Code, string Description, FindingSeverity Severity);

/// <summary>One node. <see cref="Namespace"/> is null for something outside the cluster. <see cref="Lines"/> are the details shown when the node is selected.</summary>
public sealed record NetworkNode(string Id, NetworkNodeKind Kind, string Label, string? Namespace, string Detail, ExposureLevel Exposure,
    IReadOnlyList<NetworkProblem> Problems, IReadOnlyList<string> Lines, KubeObject? Source = null, bool Missing = false, string? Note = null)
{
    public bool HasProblem => Problems.Count > 0;
    public FindingSeverity? Worst => Problems.Count == 0 ? null : Problems.Max(p => p.Severity);
    public string KindText => Kind switch { NetworkNodeKind.Exposure => "Exposure", NetworkNodeKind.Ingress => "Ingress", NetworkNodeKind.Service => "Service", _ => "Workload" };
}

/// <param name="Problem">The ports on this hop do not line up (missing service or port, unresolved target port).</param>
public sealed record NetworkEdge(string From, string To, string Label, bool Problem);

/// <summary>What is known about a TLS secret named by an Ingress. Only existence and the public certificate's expiry are read; nothing of the secret is kept.</summary>
public sealed record TlsSecretStatus(bool? Exists, DateTimeOffset? NotAfter, string? Detail = null);

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
    public bool Truncated { get; init; }
    public int Chains { get; init; }
    public int OutsideChains { get; init; }
    public int ProblemChains { get; init; }
    public string? Warning { get; init; }
}
