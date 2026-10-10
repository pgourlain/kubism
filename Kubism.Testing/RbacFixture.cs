using System.Reflection;
using System.Text.Json.Nodes;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Security;

namespace Kubism.Testing;

/// <summary>The RBAC objects of Data/rbac.yaml, split by kind, for tests that need the graph without a server.</summary>
public static class RbacFixture
{
    public static IReadOnlyList<KubeObject> Objects()
    {
        var asm = typeof(RbacFixture).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("rbac.yaml", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return YamlJson.ParseDocuments(r.ReadToEnd()).OfType<JsonObject>().Select(o => new KubeObject(o)).ToList();
    }

    public static RbacGraph Graph(Func<KubeObject, bool>? skip = null, IReadOnlyList<RbacKindStatus>? availability = null)
    {
        var all = Objects().Where(o => skip is null || !skip(o)).ToList();
        IEnumerable<KubeObject> Of(string kind) => all.Where(o => o.Kind == kind);
        return RbacGraph.Build(Of("Role"), Of("ClusterRole"), Of("RoleBinding"), Of("ClusterRoleBinding"), availability);
    }
}
