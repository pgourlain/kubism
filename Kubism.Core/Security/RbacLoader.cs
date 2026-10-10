using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>
/// One-shot graph for callers with no live cache (the MCP server): lists the four RBAC kinds through the transport. A kind that cannot be
/// listed (403, not served) is reported in <see cref="RbacGraph.Availability"/> instead of failing, so the answer says it is incomplete.
/// </summary>
public static class RbacLoader
{
    public static async Task<RbacGraph> LoadAsync(IKubeTransport transport, CancellationToken ct)
    {
        var kinds = new (string Resource, string Kind)[] { ("roles", "Role"), ("clusterroles", "ClusterRole"), ("rolebindings", "RoleBinding"), ("clusterrolebindings", "ClusterRoleBinding") };
        var sets = new List<KubeObject>[4]; var status = new List<RbacKindStatus>();
        for (var i = 0; i < kinds.Length; i++)
        {
            sets[i] = [];
            try
            {
                string? cont = null;
                do
                {
                    var page = await transport.ListAsync(new Gvr("rbac.authorization.k8s.io", "v1", kinds[i].Resource), null, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        var o = new KubeObject(item);
                        if (o.Kind.Length == 0) item["kind"] = kinds[i].Kind;
                        sets[i].Add(o);
                    }
                    cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
                } while (cont is not null);
                status.Add(new RbacKindStatus(kinds[i].Resource, true, null));
            }
            catch (Exception ex) when (ex is KubeApiException or KubeConnectionException)
            {
                sets[i] = [];
                status.Add(new RbacKindStatus(kinds[i].Resource, false, ex.Message));
            }
        }
        return RbacGraph.Build(sets[0], sets[1], sets[2], sets[3], status);
    }
}
