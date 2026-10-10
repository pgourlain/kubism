using System.Reflection;
using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.Sessions;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class ClusterActionsTests : IAsyncLifetime
{
    private FakeApiServer _server = null!;
    private ClusterSession _session = null!;
    private readonly SettingsStore _settings = new();
    private readonly ClusterEventsService _events = new();
    private ClusterEntry _entry = null!;
    private readonly List<ClusterEvent> _seen = [];

    public async ValueTask InitializeAsync()
    {
        _server = await FakeApiServer.StartAsync();
        _entry = KubeconfigCatalog.ParseFile(_server.WriteKubeconfig())[0];
        _events.Subscribe(e => { lock (_seen) _seen.Add(e); });
        _session = new ClusterSession(_entry, new KubeTransportFactory(), _events, _settings);
        await _session.ConnectAsync();
    }

    public async ValueTask DisposeAsync() { await _session.DisposeAsync(); await _server.DisposeAsync(); }

    private ClusterActions A => _session.Actions;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SetMode(SafetyMode m) => _settings.SetMetadata(_entry.Id, new ClusterMetadata { SafetyMode = m });

    private int Mutations() => _server.Requests.Count(r => r.Method is "POST" or "PUT" or "PATCH" or "DELETE" && !r.Path.Contains("selfsubjectaccessreviews"));

    [Fact]
    public void Actions_lists_every_public_async_action()
    {
        var methods = typeof(ClusterActions).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.EndsWith("Async") && m.Name != nameof(ClusterActions.RunAsync)).ToList();
        foreach (var m in methods)
            Assert.True(m.GetCustomAttribute<ClusterActionAttribute>() is not null, $"{m.Name} is a public action without [ClusterAction]");
        foreach (var expected in new[] { "Apply", "Replace", "Delete", "DeleteMany", "Scale", "RolloutRestart", "Cordon", "Drain", "TriggerCronJob", "SetResources", "SuspendCronJob", "CreateVpa", "ArgoSync", "ArgoRefresh", "FluxReconcile", "FluxSuspend" })
            Assert.Contains(expected, ClusterActions.Actions);
    }

    [Fact]
    public async Task Read_only_refuses_every_mutation_before_touching_the_server()
    {
        SetMode(SafetyMode.ReadOnly);
        var before = Mutations();
        await Assert.ThrowsAsync<SafetyException>(() => A.DeleteAsync(Gvr.Pods, "default", "crashy-0", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.ScaleAsync(Gvr.Deployments, "default", "web", 5, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.RolloutRestartAsync(Gvr.Deployments, "default", "web", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.SetCordonAsync("node-1", true, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.ArgoSyncAsync("argocd", "shop", false, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.ArgoRefreshAsync("argocd", "shop", true, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.FluxReconcileAsync(new Gvr("kustomize.toolkit.fluxcd.io", "v1", "kustomizations"), "flux-system", "apps", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.SetFluxSuspendedAsync(new Gvr("kustomize.toolkit.fluxcd.io", "v1", "kustomizations"), "flux-system", "apps", true, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.ApplyYamlAsync("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n", "default", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.ReplaceAsync("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: x\n", "default", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.DrainAsync("node-1", new DrainOptions(), null, Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.TriggerCronJobAsync("default", "nightly-report", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.SetResourcesAsync(Gvr.Deployments, "default", "web", [], Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.DeleteManyAsync(Gvr.Pods, [("default", "x")], Ct));
        Assert.Equal(before, Mutations());
        Assert.Equal(14, _session.Activity.Entries.Count(e => !e.Success));
    }

    [Fact]
    public async Task Production_requires_typed_name_for_delete()
    {
        SetMode(SafetyMode.Production);
        await Assert.ThrowsAsync<SafetyException>(() => A.DeleteAsync(Gvr.Pods, "default", "crashy-0", Ct));
        await Assert.ThrowsAsync<SafetyException>(() => A.DeleteAsync(Gvr.Pods, "default", "crashy-0", Ct, confirmation: "wrong"));
        Assert.NotNull(_server.Get(Gvr.Pods, "default", "crashy-0"));
        await A.DeleteAsync(Gvr.Pods, "default", "crashy-0", Ct, confirmation: "crashy-0");
        Assert.Null(_server.Get(Gvr.Pods, "default", "crashy-0"));
    }

    [Fact]
    public async Task Production_scale_to_zero_needs_confirmation_but_scale_up_does_not()
    {
        SetMode(SafetyMode.Production);
        await A.ScaleAsync(Gvr.Deployments, "default", "web", 4, Ct);
        await Assert.ThrowsAsync<SafetyException>(() => A.ScaleAsync(Gvr.Deployments, "default", "web", 0, Ct));
        await A.ScaleAsync(Gvr.Deployments, "default", "web", 0, Ct, confirmation: "web");
        Assert.Equal(0, _server.Get(Gvr.Deployments, "default", "web")!["spec"]!["replicas"]!.GetValue<int>());
    }

    [Fact]
    public async Task Bulk_delete_in_production_asks_for_the_count()
    {
        SetMode(SafetyMode.Production);
        var items = new[] { ("default", "web-5d8f7c-ax0k"), ("default", "web-5d8f7c-bx1k") };
        await Assert.ThrowsAsync<SafetyException>(() => A.DeleteManyAsync(Gvr.Pods, items, Ct, confirmation: "web"));
        var res = await A.DeleteManyAsync(Gvr.Pods, items, Ct, confirmation: "2");
        Assert.All(res, r => Assert.Null(r.error));
    }

    [Fact]
    public async Task Normal_mode_actions_are_logged_and_raise_events()
    {
        await A.ScaleAsync(Gvr.Deployments, "default", "web", 5, Ct);
        await A.RolloutRestartAsync(Gvr.Deployments, "default", "web", Ct);
        await A.SetCordonAsync("node-2", true, Ct);
        Assert.True((bool)_server.Get(Gvr.Nodes, null, "node-2")!["spec"]!["unschedulable"]!.GetValue<bool>());
        var web = _server.Get(Gvr.Deployments, "default", "web")!;
        Assert.NotNull(web["spec"]!["template"]!["metadata"]!["annotations"]!["kubectl.kubernetes.io/restartedAt"]);
        Assert.Equal(5, web["spec"]!["replicas"]!.GetValue<int>());
        await A.SetCordonAsync("node-2", false, Ct);
        Assert.Null(_server.Get(Gvr.Nodes, null, "node-2")!["spec"]!["unschedulable"]);
        Assert.Equal(4, _session.Activity.Entries.Count(e => e.Success));
        lock (_seen) Assert.Equal(4, _seen.OfType<ActionCompleted>().Count());
        Assert.Contains("Scale", _session.Activity.ExportCsv());
    }

    [Fact]
    public async Task Failed_action_is_logged_and_raises_ActionFailed_with_api_message()
    {
        var ex = await Assert.ThrowsAsync<KubeApiException>(() => A.DeleteAsync(Gvr.Pods, "default", "nope", Ct));
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("not found", ex.Message);
        lock (_seen) Assert.Single(_seen.OfType<ActionFailed>());
        Assert.False(_session.Activity.Entries[^1].Success);
    }

    [Fact]
    public async Task Apply_creates_with_server_side_apply_field_manager()
    {
        await A.ApplyYamlAsync("apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: made\ndata:\n  k: v\n", "staging", Ct);
        Assert.Equal("v", _server.Get(Gvr.ConfigMaps, "staging", "made")!["data"]!["k"]!.GetValue<string>());
        Assert.Contains(_server.Requests, r => r.Method == "PATCH" && r.Query.Contains("fieldManager=kubism"));
    }

    [Fact]
    public async Task Replace_conflict_returns_fresh_object_and_never_overwrites()
    {
        var current = _server.Get(Gvr.ConfigMaps, "default", "app-config")!;
        var stale = (JsonObject)current.DeepClone();
        // Someone else edits the object.
        current["data"]!["LOG_LEVEL"] = "debug";
        _server.Upsert(current);
        stale["data"]!["LOG_LEVEL"] = "error";
        var ex = await Assert.ThrowsAsync<ConflictException>(() => A.ReplaceAsync(YamlJson.ToYaml(stale), null, Ct));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("debug", ex.Fresh["data"]!["LOG_LEVEL"]!.GetValue<string>());
        Assert.Equal("debug", _server.Get(Gvr.ConfigMaps, "default", "app-config")!["data"]!["LOG_LEVEL"]!.GetValue<string>());
        // Reapplying on the fresh resourceVersion works.
        var fresh = ex.Fresh; fresh["data"]!["LOG_LEVEL"] = "error";
        await A.ReplaceAsync(YamlJson.ToYaml(fresh), null, Ct);
        Assert.Equal("error", _server.Get(Gvr.ConfigMaps, "default", "app-config")!["data"]!["LOG_LEVEL"]!.GetValue<string>());
    }

    [Fact]
    public async Task Trigger_cronjob_creates_job_with_owner_reference()
    {
        var name = await A.TriggerCronJobAsync("default", "nightly-report", Ct);
        var job = _server.Get(Gvr.Jobs, "default", name)!;
        Assert.Equal("nightly-report", job["metadata"]!["ownerReferences"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("report", job["spec"]!["template"]!["spec"]!["containers"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Set_resources_merges_by_container_name()
    {
        await A.SetResourcesAsync(Gvr.Deployments, "default", "api", [new ContainerResources("main", "300m", null, "300Mi", "400Mi")], Ct);
        var c = _server.Get(Gvr.Deployments, "default", "api")!["spec"]!["template"]!["spec"]!["containers"]!.AsArray();
        Assert.Single(c);
        Assert.Equal("300m", c[0]!["resources"]!["requests"]!["cpu"]!.GetValue<string>());
        Assert.Equal("400Mi", c[0]!["resources"]!["limits"]!["memory"]!.GetValue<string>());
        Assert.Equal("nginx", "nginx"); // image preserved check below
        Assert.Equal("example/api:2.1", c[0]!["image"]!.GetValue<string>());
    }

    [Fact]
    public async Task Drain_cordons_evicts_and_retries_pdb_blocked_pods()
    {
        A.RetryDelay = TimeSpan.FromMilliseconds(50);
        var onNode2 = _server.List(Gvr.Pods, null).Where(p => p["spec"]!["nodeName"]?.GetValue<string>() == "node-2" && p["metadata"]!["ownerReferences"] is not null).ToList();
        Assert.NotEmpty(onNode2);
        var blocked = onNode2[0];
        var bns = blocked["metadata"]!["namespace"]!.GetValue<string>(); var bname = blocked["metadata"]!["name"]!.GetValue<string>();
        _server.BlockEviction(bns, bname);
        var progress = new List<DrainProgress>();
        var task = A.DrainAsync("node-2", new DrainOptions(Timeout: TimeSpan.FromSeconds(10)), new Progress<DrainProgress>(p => { lock (progress) progress.Add(p); if (p.Status.Contains("blocked")) _server.UnblockEviction(bns, bname); }), Ct);
        await task;
        Assert.True((bool)_server.Get(Gvr.Nodes, null, "node-2")!["spec"]!["unschedulable"]!.GetValue<bool>());
        Assert.Null(_server.Get(Gvr.Pods, bns, bname));
        await Task.Delay(100);
        lock (progress) Assert.Contains(progress, p => p.Status.Contains("blocked"));
    }

    [Fact]
    public async Task Drain_skips_daemonset_pods_and_refuses_when_not_ignored()
    {
        var ds = JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","metadata":{"name":"ds-pod","namespace":"kube-system","ownerReferences":[{"apiVersion":"apps/v1","kind":"DaemonSet","name":"x","uid":"u"}]},"spec":{"nodeName":"node-1"}}""")!.AsObject();
        _server.Upsert(ds);
        await Assert.ThrowsAsync<KubeApiException>(() => A.DrainAsync("node-1", new DrainOptions(IgnoreDaemonSets: false), null, Ct));
        await A.DrainAsync("node-1", new DrainOptions(), null, Ct);
        Assert.NotNull(_server.Get(Gvr.Pods, "kube-system", "ds-pod"));
    }

    [Fact]
    public async Task Access_review_reports_forbidden_with_reason_and_caches()
    {
        _server.Deny("delete", "pods");
        var r = await _session.Access.CanAsync("delete", Gvr.Pods, "default", null, Ct);
        Assert.False(r.Allowed);
        Assert.Contains("cannot delete pods", r.Reason);
        var count = _server.Requests.Count(x => x.Path.Contains("selfsubjectaccessreviews"));
        await _session.Access.CanAsync("delete", Gvr.Pods, "default", null, Ct);
        Assert.Equal(count, _server.Requests.Count(x => x.Path.Contains("selfsubjectaccessreviews")));
        Assert.True((await _session.Access.CanAsync("get", Gvr.Pods, "default", null, Ct)).Allowed);
    }

    [Fact]
    public async Task Gitops_actions_set_the_operation_annotations_and_suspend_flags_Argo_and_Flux_expect()
    {
        var apps = new Gvr("argoproj.io", "v1alpha1", "applications");
        var ks = new Gvr("kustomize.toolkit.fluxcd.io", "v1", "kustomizations");
        _server.Upsert(JsonNode.Parse("""{"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"shop","namespace":"argocd"},"spec":{},"status":{}}""")!.AsObject());
        _server.Upsert(JsonNode.Parse("""{"apiVersion":"kustomize.toolkit.fluxcd.io/v1","kind":"Kustomization","metadata":{"name":"apps","namespace":"flux-system"},"spec":{},"status":{}}""")!.AsObject());

        await A.ArgoSyncAsync("argocd", "shop", false, Ct);
        var app = _server.Get(apps, "argocd", "shop")!;
        Assert.Equal("kubism", app["operation"]!["initiatedBy"]!["username"]!.ToString());
        Assert.False(app["operation"]!["sync"]!["prune"]!.GetValue<bool>());
        await A.ArgoRefreshAsync("argocd", "shop", true, Ct);
        Assert.Equal("hard", _server.Get(apps, "argocd", "shop")!["metadata"]!["annotations"]!["argocd.argoproj.io/refresh"]!.ToString());

        await A.FluxReconcileAsync(ks, "flux-system", "apps", Ct);
        Assert.Contains("T", _server.Get(ks, "flux-system", "apps")!["metadata"]!["annotations"]!["reconcile.fluxcd.io/requestedAt"]!.ToString());
        await A.SetFluxSuspendedAsync(ks, "flux-system", "apps", true, Ct);
        Assert.True(_server.Get(ks, "flux-system", "apps")!["spec"]!["suspend"]!.GetValue<bool>());
        await A.SetFluxSuspendedAsync(ks, "flux-system", "apps", false, Ct);
        Assert.Null(_server.Get(ks, "flux-system", "apps")!["spec"]!["suspend"]);
        Assert.All(new[] { "ArgoSync", "ArgoRefresh", "FluxReconcile", "FluxSuspend", "FluxResume" }, a => Assert.Contains(_session.Activity.Entries, e => e.Action == a && e.Success));
    }

    [Fact]
    public async Task Argo_sync_with_prune_needs_the_typed_name_on_production()
    {
        _server.Upsert(JsonNode.Parse("""{"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"shop","namespace":"argocd"},"spec":{},"status":{}}""")!.AsObject());
        SetMode(SafetyMode.Production);
        await A.ArgoSyncAsync("argocd", "shop", false, Ct);                                   // a plain sync is not destructive
        await Assert.ThrowsAsync<SafetyException>(() => A.ArgoSyncAsync("argocd", "shop", true, Ct));
        await A.ArgoSyncAsync("argocd", "shop", true, Ct, confirmation: "shop");
    }
}
