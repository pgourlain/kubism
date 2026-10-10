using System.Text.Json.Nodes;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Network;
using Kubism.Core.Security;

namespace Kubism.Testing;

/// <summary>The objects of Data/network.yaml, split by kind, for tests that need the network graph without a server.</summary>
public static class NetworkFixture
{
    public static IReadOnlyList<KubeObject> Objects()
    {
        var asm = typeof(NetworkFixture).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("network.yaml", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return YamlJson.ParseDocuments(r.ReadToEnd()).OfType<JsonObject>().Select(o => new KubeObject(o)).ToList();
    }

    public static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>shop-tls expires in 10 days, admin-tls does not exist; other secrets were not checked.</summary>
    public static IReadOnlyDictionary<(string Namespace, string Secret), TlsSecretStatus> Tls() => new Dictionary<(string, string), TlsSecretStatus>
    {
        [("shop", "shop-tls")] = new(true, Now.AddDays(10)),
        [("shop", "admin-tls")] = new(false, null),
    };

    /// <summary>The two nodes the fixture's pods run on. The demo environment has nodes of the same names, so UI and MCP tests see the same.</summary>
    public static IReadOnlyList<KubeObject> Nodes(bool node2Ready = true) =>
    [
        Node("node-1", true), Node("node-2", node2Ready),
    ];

    private static KubeObject Node(string name, bool ready) => new(new JsonObject
    {
        ["apiVersion"] = "v1", ["kind"] = "Node", ["metadata"] = new JsonObject { ["name"] = name },
        ["status"] = new JsonObject { ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Ready", ["status"] = ready ? "True" : "False" }) },
    });

    public static NetworkGraph Graph(Func<KubeObject, bool>? skip = null, IReadOnlyList<RbacKindStatus>? availability = null, bool tls = true, IReadOnlyList<KubeObject>? nodes = null, bool unreadableNodes = false)
    {
        var all = Objects().Where(o => skip is null || !skip(o)).ToList();
        IEnumerable<KubeObject> Of(string kind) => all.Where(o => o.Kind == kind);
        return NetworkGraph.Build(Of("Ingress"), Of("IngressClass"), Of("Service"), Of("EndpointSlice"), Of("Pod"), tls ? Tls() : null, availability, Now, unreadableNodes ? null : nodes ?? Nodes());
    }
}
