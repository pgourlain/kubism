using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Delivery;
using Xunit;

namespace Kubism.Core.Tests;

public class GitOpsTests
{
    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());
    private static readonly Gvr Argo = new("argoproj.io", "v1alpha1", "applications");
    private static readonly Gvr Ks = new("kustomize.toolkit.fluxcd.io", "v1", "kustomizations");
    private static readonly Gvr Hr = new("helm.toolkit.fluxcd.io", "v2", "helmreleases");

    [Fact]
    public void Argo_application_is_reduced_to_sync_health_source_and_resources()
    {
        var app = GitOpsApp.From(O("""{"kind":"Application","metadata":{"name":"payments","namespace":"argocd"},"spec":{"source":{"repoURL":"https://git.example.com/pay.git","path":"deploy/prod","targetRevision":"main"},"destination":{"server":"https://kubernetes.default.svc","namespace":"payments"}},"status":{"sync":{"status":"OutOfSync","revision":"0123456789abcdef0123456789abcdef01234567"},"health":{"status":"Degraded"},"operationState":{"finishedAt":"2026-10-10T08:00:00Z"},"resources":[{"kind":"Deployment","namespace":"payments","name":"api","status":"OutOfSync","health":{"status":"Degraded","message":"0/2 ready"}},{"kind":"Service","namespace":"payments","name":"api","status":"Synced","health":{"status":"Healthy"}}]}}"""), Argo)!;
        Assert.Equal(GitOpsTool.ArgoCd, app.Tool); Assert.Equal("Argo CD", app.ToolName);
        Assert.Equal("OutOfSync", app.Sync); Assert.Equal("Degraded", app.Health); Assert.Equal(GitOpsLevel.Degraded, app.Level);
        Assert.Equal("https://git.example.com/pay.git · deploy/prod", app.Source);
        Assert.Equal("https://kubernetes.default.svc / payments", app.Destination);
        Assert.Equal("01234567", app.Revision);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero), app.LastChange);
        Assert.Equal("argocd/payments", app.Display);
        Assert.Equal(1, app.ProblemResources);
        Assert.Equal("Deployment/payments/api", app.Resources[0].Display);   // problems first
        Assert.True(app.IsProblem);
    }

    [Theory]
    [InlineData("Synced", "Healthy", GitOpsLevel.Healthy)]
    [InlineData("OutOfSync", "Healthy", GitOpsLevel.OutOfSync)]
    [InlineData("Synced", "Progressing", GitOpsLevel.Progressing)]
    [InlineData("Synced", "Missing", GitOpsLevel.Degraded)]
    [InlineData("Synced", "Suspended", GitOpsLevel.Suspended)]
    [InlineData("Unknown", "Unknown", GitOpsLevel.Unknown)]
    public void Argo_level_combines_sync_and_health(string sync, string health, GitOpsLevel level)
    {
        var o = O("""{"kind":"Application","metadata":{"name":"a","namespace":"argocd"},"status":{"sync":{"status":"SYNC"},"health":{"status":"HEALTH"}}}""".Replace("SYNC", sync).Replace("HEALTH", health));
        Assert.Equal(level, GitOpsApp.FromArgo(o, Argo).Level);
    }

    [Fact]
    public void Argo_error_conditions_become_the_message()
    {
        var o = O("""{"kind":"Application","metadata":{"name":"a","namespace":"argocd"},"status":{"sync":{"status":"Unknown"},"conditions":[{"type":"ComparisonError","message":"rpc error: repository not found"}]}}""");
        var app = GitOpsApp.FromArgo(o, Argo);
        Assert.Equal("rpc error: repository not found", app.Message);
        Assert.Equal(GitOpsLevel.Degraded, app.Level);   // a comparison error is a problem even though Argo reports no health
    }

    [Fact]
    public void Flux_kustomization_uses_its_ready_condition_inventory_and_revision()
    {
        var app = GitOpsApp.From(O("""{"kind":"Kustomization","metadata":{"name":"apps","namespace":"flux-system"},"spec":{"sourceRef":{"kind":"GitRepository","name":"flux-system"},"path":"./apps"},"status":{"lastAppliedRevision":"main@sha1:0123456789abcdef0123456789abcdef01234567","conditions":[{"type":"Ready","status":"False","message":"health check failed after 5m: Deployment/web not ready","lastTransitionTime":"2026-10-10T07:00:00Z"}],"inventory":{"entries":[{"id":"default_web_apps_Deployment"},{"id":"_staging__Namespace"},{"id":"default_my_cfg_map__ConfigMap"}]}}}"""), Ks)!;
        Assert.Equal(GitOpsTool.Flux, app.Tool);
        Assert.Equal("Not ready", app.Sync); Assert.Equal(GitOpsLevel.Degraded, app.Level);
        Assert.Equal("GitRepository/flux-system · ./apps", app.Source);
        Assert.Equal("main@sha1:01234567", app.Revision);
        Assert.Contains("health check failed", app.Message);
        Assert.Equal(["ConfigMap/default/my_cfg_map", "Deployment/default/web", "Namespace/staging"], app.Resources.Select(r => r.Display));
    }

    [Fact]
    public void Flux_suspended_and_reconciling_states_and_helmrelease_source()
    {
        var suspended = GitOpsApp.FromFlux(O("""{"kind":"HelmRelease","metadata":{"name":"podinfo","namespace":"flux-system"},"spec":{"suspend":true,"chart":{"spec":{"chart":"podinfo","version":"6.x","sourceRef":{"name":"podinfo"}}}},"status":{"conditions":[{"type":"Ready","status":"True"}]}}"""), Hr);
        Assert.True(suspended.Suspended); Assert.Equal(GitOpsLevel.Suspended, suspended.Level);
        Assert.Equal("podinfo@6.x from podinfo", suspended.Source);
        var reconciling = GitOpsApp.FromFlux(O("""{"kind":"HelmRelease","metadata":{"name":"x","namespace":"n"},"spec":{},"status":{"conditions":[{"type":"Ready","status":"Unknown","message":"Reconciliation in progress"}]}}"""), Hr);
        Assert.Equal(GitOpsLevel.Progressing, reconciling.Level); Assert.Equal("Reconciling", reconciling.Sync);
        Assert.Equal(GitOpsLevel.Progressing, GitOpsApp.FromFlux(O("""{"kind":"GitRepository","metadata":{"name":"g","namespace":"n"},"spec":{"url":"https://x"}}"""), new Gvr("source.toolkit.fluxcd.io", "v1", "gitrepositories")).Level);
    }

    [Fact]
    public void Gitops_kinds_are_recognised_by_group_and_resource()
    {
        Assert.True(GitOpsApp.IsGitOpsKind("argoproj.io", "applications"));
        Assert.True(GitOpsApp.IsGitOpsKind("kustomize.toolkit.fluxcd.io", "kustomizations"));
        Assert.False(GitOpsApp.IsGitOpsKind("argoproj.io", "appprojects"));
        Assert.Null(GitOpsApp.From(O("""{"kind":"Pod","metadata":{"name":"p"}}"""), Gvr.Pods));
    }
}
