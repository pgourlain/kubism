using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Security;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class RbacStoreTests
{
    private static readonly Gvr Roles = new("rbac.authorization.k8s.io", "v1", "roles");
    private static readonly Gvr ClusterRoles = new("rbac.authorization.k8s.io", "v1", "clusterroles");
    private static readonly Gvr RoleBindings = new("rbac.authorization.k8s.io", "v1", "rolebindings");
    private static readonly Gvr ClusterRoleBindings = new("rbac.authorization.k8s.io", "v1", "clusterrolebindings");

    private static readonly InformerOptions Fast = new() { Coalesce = TimeSpan.FromMilliseconds(10), BackoffBase = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(50) };

    private static FakeKubeTransport Seed()
    {
        var t = new FakeKubeTransport();
        var all = RbacFixture.Objects();
        JsonObject[] Of(string kind) => all.Where(o => o.Kind == kind).Select(o => (JsonObject)o.Json.DeepClone()).ToArray();
        t.Set(Roles, Of("Role")); t.Set(ClusterRoles, Of("ClusterRole")); t.Set(RoleBindings, Of("RoleBinding")); t.Set(ClusterRoleBindings, Of("ClusterRoleBinding"));
        return t;
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }

    [Fact]
    public async Task The_graph_is_built_from_the_cache_and_follows_changes()
    {
        var t = Seed();
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new RbacStore(cache, TimeSpan.FromMilliseconds(20));
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(store.Graph.IsComplete);
        Assert.Contains(store.Graph.Bindings, b => b.Name == "devs-edit");
        Assert.False(store.Graph.Explain(new RbacSubject("User", "newbie"), RbacRequest.Parse("get", "pods", "staging")).Allowed);

        // a new RoleBinding arrives through the watch (once the four watches are open: an event sent before is lost, as on a real server)
        Assert.True(await Until(() => t.ActiveWatches >= 4));
        var binding = t.Make("RoleBinding", "newbie-view", "staging", apiVersion: "rbac.authorization.k8s.io/v1");
        binding["roleRef"] = new JsonObject { ["kind"] = "ClusterRole", ["name"] = "view" };
        binding["subjects"] = new JsonArray(new JsonObject { ["kind"] = "User", ["name"] = "newbie" });
        var changed = 0;
        store.Changed += _ => Interlocked.Increment(ref changed);
        t.Emit(WatchEventType.Added, binding);
        Assert.True(await Until(() => store.Graph.Explain(new RbacSubject("User", "newbie"), RbacRequest.Parse("get", "pods", "staging")).Allowed));
        Assert.True(await Until(() => Volatile.Read(ref changed) > 0));      // Changed is raised just after the graph is replaced

        // and goes away again
        t.Emit(WatchEventType.Deleted, binding);
        Assert.True(await Until(() => !store.Graph.Bindings.Any(b => b.Name == "newbie-view")));
    }

    [Fact]
    public async Task A_kind_the_user_cannot_list_is_reported_as_missing()
    {
        var t = Seed();
        t.ListFailure = (gvr, _) => gvr == RoleBindings ? new KubeApiException(403, "Forbidden", "rolebindings.rbac.authorization.k8s.io is forbidden: User \"u\" cannot list resource \"rolebindings\"") : null;
        await using var cache = new ResourceCache(t, "c", options: Fast);
        using var store = new RbacStore(cache, TimeSpan.FromMilliseconds(20));
        await store.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(store.Graph.IsComplete);
        Assert.Equal(["rolebindings"], store.Graph.MissingKinds);
        Assert.Contains("forbidden", store.Graph.Availability.Single(a => a.Kind == "rolebindings").Error);
        Assert.Contains("Incomplete data", store.Graph.IncompleteWarning);
        Assert.DoesNotContain(store.Graph.Bindings, b => b.Kind == "RoleBinding");
    }

    // ---------- verification against the API server ----------

    private static FakeKubeTransport ApiServer(string user, string[] groups, Func<JsonObject, bool> allowed, bool reviewsAllowed = true)
    {
        var t = new FakeKubeTransport();
        t.SendHandler = (method, path, body) =>
        {
            var b = body is null ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
            if (path.EndsWith("/selfsubjectreviews"))
                return new KubeResponse(201, new JsonObject { ["status"] = new JsonObject { ["userInfo"] = new JsonObject { ["username"] = user, ["groups"] = new JsonArray(groups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) } } }.ToJsonString());
            if (path.EndsWith("/subjectaccessreviews") && !path.EndsWith("/selfsubjectaccessreviews") && !reviewsAllowed) return new KubeResponse(403, "{}");
            if (path.EndsWith("subjectaccessreviews"))
            {
                var spec = (JsonObject)b["spec"]!;
                // the access review for "create subjectaccessreviews" itself
                if (spec["resourceAttributes"]?["resource"]?.ToString() == "subjectaccessreviews")
                    return new KubeResponse(201, new JsonObject { ["status"] = new JsonObject { ["allowed"] = reviewsAllowed } }.ToJsonString());
                return new KubeResponse(201, new JsonObject { ["status"] = new JsonObject { ["allowed"] = allowed(spec), ["reason"] = "test" } }.ToJsonString());
            }
            return new KubeResponse(404, "{}");
        };
        return t;
    }

    [Fact]
    public async Task Self_verification_compares_the_graph_with_selfsubjectaccessreview_and_reports_a_gap()
    {
        var graph = RbacFixture.Graph();
        // the API server agrees with RBAC for staging, but also allows production (another authorizer, e.g. Azure RBAC)
        var t = ApiServer("erin", ["devs", "system:authenticated"], spec => spec["resourceAttributes"]?["namespace"]?.ToString() is "staging" or "production");
        var v = new RbacVerifier(t, new AccessReview(t));

        var same = await v.VerifySelfAsync(graph, RbacRequest.Parse("create", "pods/exec", "staging"), CancellationToken.None);
        Assert.Equal((true, true, false), (same.Computed, same.Server, same.Mismatch));
        Assert.Contains("agrees", same.Note);

        var gap = await v.VerifySelfAsync(graph, RbacRequest.Parse("create", "pods/exec", "production"), CancellationToken.None);
        Assert.Equal((false, true, true), (gap.Computed, gap.Server, gap.Mismatch));
        Assert.Contains("another authorizer", gap.Note);

        var id = await v.WhoAmIAsync(CancellationToken.None);
        Assert.Equal("erin", id!.UserName);
        Assert.Contains("devs", id.Groups);
    }

    [Fact]
    public async Task Without_selfsubjectreview_the_server_answer_is_shown_but_not_compared()
    {
        var t = new FakeKubeTransport { SendHandler = (_, path, _) => path.EndsWith("selfsubjectreviews") ? new KubeResponse(404, "{}") : new KubeResponse(201, """{"status":{"allowed":true}}""") };
        var v = new RbacVerifier(t, new AccessReview(t));
        var r = await v.VerifySelfAsync(RbacFixture.Graph(), RbacRequest.Parse("get", "pods", "staging"), CancellationToken.None);
        Assert.Null(r.Computed); Assert.True(r.Server); Assert.Null(r.Mismatch);
        Assert.Contains("identity could not be read", r.Note);
    }

    [Fact]
    public async Task Subject_access_review_for_other_subjects_needs_the_right_to_create_them()
    {
        var graph = RbacFixture.Graph();
        var denied = ApiServer("me", [], _ => true, reviewsAllowed: false);
        var v1 = new RbacVerifier(denied, new AccessReview(denied));
        var no = await v1.VerifySubjectAsync(graph, new RbacSubject("User", "bob"), RbacRequest.Parse("create", "pods/exec", "production"), CancellationToken.None);
        Assert.Null(no.Server); Assert.Equal(true, no.Computed);
        Assert.Contains("cannot create subjectaccessreviews", no.Note);
        Assert.False((await v1.CanReviewOthersAsync(CancellationToken.None)).Allowed);

        var allowed = ApiServer("me", [], spec => spec["user"]?.ToString() == "bob" && spec["resourceAttributes"]?["namespace"]?.ToString() == "production");
        var v2 = new RbacVerifier(allowed, new AccessReview(allowed));
        var yes = await v2.VerifySubjectAsync(graph, new RbacSubject("User", "bob"), RbacRequest.Parse("create", "pods/exec", "production"), CancellationToken.None);
        Assert.Equal((true, true, false), (yes.Computed, yes.Server, yes.Mismatch));
    }
}
