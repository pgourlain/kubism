using Kubism.Core.Api;
using Kubism.Core.Discovery;

namespace Kubism.Core.Network;

/// <summary>One Gateway API kind found on a cluster, at the version to read it with.</summary>
public sealed record GatewayKind(Gvr Gvr, string Kind, string Name);

/// <summary>The Gateway API objects the network graph reads. Anything may be empty: the CRDs are optional and a user may not be allowed to list them.</summary>
public sealed record GatewayObjects(
    IReadOnlyList<KubeObject> Classes, IReadOnlyList<KubeObject> Gateways, IReadOnlyList<KubeObject> Routes, IReadOnlyList<KubeObject> Grants)
{
    public static readonly GatewayObjects None = new([], [], [], []);
    public bool IsEmpty => Classes.Count == 0 && Gateways.Count == 0 && Routes.Count == 0;
}

/// <summary>Which Gateway API kinds the network graph reads, and how to find them on a cluster.</summary>
public static class GatewayApi
{
    public const string Group = "gateway.networking.k8s.io";

    /// <summary>The kinds that carry traffic to a backend, in the order the sidebar and the graph list them.</summary>
    public static readonly IReadOnlyList<string> RouteKinds = ["HTTPRoute", "GRPCRoute", "TLSRoute", "TCPRoute", "UDPRoute"];

    private static readonly string[] Read = ["GatewayClass", "Gateway", .. RouteKinds, "ReferenceGrant"];
    private static readonly string[] VersionPreference = ["v1", "v1beta1", "v1alpha3", "v1alpha2"];

    /// <summary>Plural resource names, for the usual versions of each kind, to probe a cluster whose discovery is not at hand (the MCP loader).</summary>
    public static IReadOnlyList<GatewayKind> Candidates(string kind) =>
        VersionPreference.Select(v => new GatewayKind(new Gvr(Group, v, Resource(kind)), kind, Resource(kind))).ToList();

    public static IReadOnlyList<string> Kinds => Read;

    public static string Resource(string kind) => kind switch
    {
        "GatewayClass" => "gatewayclasses", "ReferenceGrant" => "referencegrants", _ => kind.ToLowerInvariant() + "s",
    };

    /// <summary>The Gateway API kinds the cluster serves and the user may list, each at its best version. Empty when the CRDs are not installed.</summary>
    public static IReadOnlyList<GatewayKind> Find(IEnumerable<ResourceKind> discovered)
    {
        var result = new List<GatewayKind>();
        foreach (var kind in Read)
        {
            var best = discovered.Where(k => k.Group == Group && k.Kind == kind && k.CanList)
                .OrderBy(k => { var i = Array.IndexOf(VersionPreference, k.Version); return i < 0 ? int.MaxValue : i; }).FirstOrDefault();
            if (best is not null) result.Add(new GatewayKind(best.Gvr, kind, best.Resource));
        }
        return result;
    }

    /// <summary>Groups the objects read for each kind into the input the graph takes (every route kind together).</summary>
    public static GatewayObjects FromKinds(IReadOnlyDictionary<string, List<KubeObject>> byKind)
    {
        IReadOnlyList<KubeObject> Of(string kind) => byKind.TryGetValue(kind, out var l) ? l : [];
        return new GatewayObjects(Of("GatewayClass"), Of("Gateway"), RouteKinds.SelectMany(Of).ToList(), Of("ReferenceGrant"));
    }

    /// <summary>True for a kind this graph reads (the Usage page and the sidebar use it to tell Gateway API objects from another group's Gateway).</summary>
    public static bool IsGatewayApi(string group) => group == Group;
}
