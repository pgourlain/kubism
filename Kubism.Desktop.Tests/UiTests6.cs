using Avalonia.Headless.XUnit;
using Kubism.Core.Views;
using Kubism.Desktop.Controls;
using Kubism.Desktop.ViewModels;
using Kubism.Core.Usage;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiExplorationTests
{
    [AvaloniaFact]
    public async Task Graph_tab_draws_the_neighbourhood_of_a_pod_and_navigates_on_click()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "pods");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name.StartsWith("web-"));
        var d = ws.Details;
        d.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => d.Graph is { Nodes.Count: > 1 }, what: "graph");
        var g = d.Graph!;
        Assert.Contains(g.Nodes, n => n.Kind == "Deployment" && n.Name == "web");
        Assert.Contains(g.Nodes, n => n.Kind == "ReplicaSet");
        Assert.Contains(g.Nodes, n => n.Kind == "Service" && n.Name == "web");
        Assert.Contains(g.Edges, e => e.Label == "selects");
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "Secret" && n.Name != "db-credentials");
        Assert.NotNull(h.Find<RelationGraphControl>());
        h.Screenshot("30-relation-graph");

        d.OpenGraphNodeCommand.Execute(g.Nodes.First(n => n.Kind == "Deployment"));
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l2 && l2.Kind.Kind == "Deployment", what: "deployments list");
        Assert.Equal("web", ((ResourceListViewModel)ws.CurrentContent!).Filter);
    }

    [AvaloniaFact]
    public async Task Graph_never_loads_secret_values()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("secrets")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "secrets");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First();
        ws.Details.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => ws.Details.Graph is not null, what: "graph");
        Assert.Contains(ws.Details.Graph!.Nodes, n => n.IsRoot && n.Kind == "Secret");
        Assert.DoesNotContain("data", ws.Details.GraphStatus);
    }

    [AvaloniaFact]
    public async Task Global_search_by_image_lists_workloads_with_the_matching_image()
    {
        await using var h = await UiHarness.StartAsync();
        await h.ConnectAsync("demo-staging");
        h.Main.OpenGlobalSearchCommand.Execute(null);
        h.Main.Search.Query = "image:nginx";
        await h.Main.Search.SearchAsync();
        var group = Assert.Single(h.Main.Search.Groups);
        Assert.Contains(group.Hits, x => x.Kind == "Deployment" && x.Name == "web" && x.Detail!.Contains("nginx"));
        Assert.Contains(group.Hits, x => x.Kind == "Pod");
        Assert.All(group.Hits, x => Assert.Contains("nginx", x.Detail!));
        h.Main.Search.Query = "ns:production kind:deploy";
        await h.Main.Search.SearchAsync();
        Assert.All(h.Main.Search.Groups.SelectMany(g => g.Hits), x => { Assert.Equal("Deployment", x.Kind); Assert.Equal("production", x.Namespace); });
        Assert.Contains(h.Main.Search.Groups.SelectMany(g => g.Hits), x => x.Name == "checkout");
        h.Main.Search.Query = "image:nginx kind:deploy";
        await h.Main.Search.SearchAsync();
        h.Screenshot("32-search-by-image");
    }

    [AvaloniaFact]
    public async Task Compare_shows_drift_between_two_clusters_without_leaking_secrets()
    {
        await using var h = await UiHarness.StartAsync(withDrift: true);
        var staging = await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-dev");
        h.Main.SelectedWorkspace = staging;
        staging.Namespaces.Select("default");
        staging.Navigate(staging.FindNav(NavTarget.Compare)!);
        await UiHarness.WaitFor(() => staging.CurrentContent is CompareViewModel, what: "compare view");
        var cmp = (CompareViewModel)staging.CurrentContent!;
        Assert.Equal("demo-dev", cmp.Target.Title);
        cmp.Kind = cmp.Kinds.First(k => k.Left.Resource == "deployments");
        await cmp.CompareAsync();
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "web", Status: CompareStatus.Different });
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "api", Status: CompareStatus.OnlyLeft });
        Assert.Contains(cmp.Rows, r => r.Entry is { Name: "legacy-importer", Status: CompareStatus.OnlyRight });
        cmp.Selected = cmp.Rows.First(r => r.Entry.Name == "web");
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("nginx:1.27"));
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("nginx:1.25"));
        h.Screenshot("31-compare-clusters");

        cmp.Kind = cmp.Kinds.First(k => k.Left.Resource == "secrets");
        await cmp.CompareAsync();
        cmp.ShowIdentical = true;
        var secret = cmp.Rows.First(r => r.Entry.Name == "db-credentials");
        Assert.Equal(CompareStatus.Different, secret.Entry.Status);
        cmp.Selected = secret;
        Assert.DoesNotContain(cmp.DiffLines, l => l.Text.Contains("another-value") || l.Text.Contains("YW5vdGhlci12YWx1ZQ"));
        Assert.Contains(cmp.DiffLines, l => l.Text.Contains("•••• A"));
    }

    [AvaloniaFact]
    public async Task Compare_from_an_object_prefills_kind_namespace_and_name()
    {
        await using var h = await UiHarness.StartAsync(withDrift: true);
        var staging = await h.ConnectAsync("demo-staging");
        await h.ConnectAsync("demo-dev");
        h.Main.SelectedWorkspace = staging;
        staging.Namespaces.Select("default");
        staging.OpenKind(staging.Session.Discovery.FindByName("configmaps")!);
        await UiHarness.WaitFor(() => staging.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "configmaps");
        var list = (ResourceListViewModel)staging.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "app-config");
        await UiHarness.WaitFor(() => staging.Details.Actions.Any(a => a.Header == "Compare…"), what: "compare action");
        await staging.Details.Actions.First(a => a.Header == "Compare…").ExecuteAsync();
        await UiHarness.WaitFor(() => staging.CurrentContent is CompareViewModel c && c.Rows.Count > 0, what: "compare rows");
        var cmp = (CompareViewModel)staging.CurrentContent!;
        Assert.Equal("ConfigMap", cmp.Kind!.Left.Kind);
        Assert.Equal("app-config", cmp.NameFilter);
        var row = Assert.Single(cmp.Rows);
        Assert.Equal(CompareStatus.Different, row.Entry.Status);
        Assert.Contains(cmp.DiffLines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("LOG_LEVEL"));
    }

    [AvaloniaFact]
    public async Task Graph_shows_the_helm_release_of_an_object_and_opens_it()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "pods");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "crashy-0");
        ws.Details.SelectedTab = DetailsViewModel.GraphTab;
        await UiHarness.WaitFor(() => ws.Details.Graph is { Nodes.Count: > 1 }, what: "graph");
        var release = ws.Details.Graph!.Nodes.Single(n => n.Kind == "HelmRelease");
        Assert.Equal("payments", release.Name);
        Assert.Equal(NodeHealth.Error, release.Health);
        Assert.StartsWith("rev 2", release.Status);
        Assert.DoesNotContain(ws.Details.Graph.Nodes, n => n.Kind == "Secret");   // release payloads are not graph objects
        h.Screenshot("33-graph-helm");

        ws.Details.OpenGraphNodeCommand.Execute(release);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Selected?.Name == "payments", what: "helm page");
    }

    [AvaloniaFact]
    public async Task Helm_release_graph_lists_what_it_rendered_and_what_runs_below()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Releases.Any(r => r.Name == "api"), what: "releases");
        var vm = (HelmViewModel)ws.CurrentContent!;
        vm.Selected = vm.Releases.First(r => r.Name == "api");
        vm.Tab = HelmViewModel.GraphTabIndex;
        await UiHarness.WaitFor(() => vm.Graph is { Nodes.Count: > 3 }, what: "release graph");
        var g = vm.Graph!;
        Assert.True(g.Nodes.Single(n => n.IsRoot).Kind == "HelmRelease");
        Assert.Contains(g.Edges, e => e is { From: "HelmRelease/default/api", To: "Deployment/default/api", Label: "manages" });
        Assert.Contains(g.Edges, e => e is { From: "HelmRelease/default/api", To: "ConfigMap/default/app-config", Label: "manages" });
        Assert.Contains(g.Nodes, n => n.Kind == "Pod");
        Assert.DoesNotContain(g.Nodes, n => n.Name == "crashy-0");
        h.Screenshot("34-helm-release-graph");
    }

    [AvaloniaFact]
    public async Task Timeline_tab_shows_rollouts_events_and_oom_kills_on_one_axis()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("deployments")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0, what: "deployments");
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "web");
        var d = ws.Details;
        d.SelectedTab = DetailsViewModel.TimelineTab;
        await UiHarness.WaitFor(() => d.TimelineRows.Count > 4, what: "timeline");
        Assert.Contains(d.TimelineRows, r => r.Title == "Rollout to revision 2" && r.Detail == "nginx:1.27");
        Assert.Contains(d.TimelineRows, r => r.Title == "Rollout to revision 1" && r.Detail == "nginx:1.25");
        Assert.Contains(d.TimelineRows, r => r.Title == "ScalingReplicaSet" && r.Source == "Deployment/web");
        Assert.Contains(d.TimelineRows, r => r.KindLabel == "created" && r.Source.StartsWith("Pod/web-"));
        h.Screenshot("35-timeline");

        list = (ResourceListViewModel)ws.CurrentContent!;
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l2 && l2.Kind.Kind == "Pod" && l2.Rows.Count > 0, what: "pods");
        var pods = (ResourceListViewModel)ws.CurrentContent!;
        pods.SelectedRow = pods.Rows.First(r => r.Name == "crashy-0");
        d.SelectedTab = DetailsViewModel.TimelineTab;
        await UiHarness.WaitFor(() => d.TimelineRows.Any(r => r.KindLabel == "oom"), what: "oom entry");
        d.ProblemsOnly = true;
        Assert.All(d.TimelineRows, r => Assert.NotEqual(Severity.Neutral, r.Severity));
        Assert.Contains(d.TimelineRows, r => r.Title == "BackOff");
        h.Screenshot("36-timeline-problems");
    }

    [AvaloniaFact]
    public async Task Usage_pages_tell_used_from_unreferenced_config_and_storage()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.SeedYaml("""
            {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"shop","namespace":"usage-demo","uid":"u-shop"},"spec":{"replicas":1,"template":{"spec":{"volumes":[{"name":"d","persistentVolumeClaim":{"claimName":"shop-data"}},{"name":"x","configMap":{"name":"deleted-cfg"}}],"containers":[{"name":"c","image":"x:1","envFrom":[{"configMapRef":{"name":"shop-cfg"}}],"env":[{"name":"K","valueFrom":{"secretKeyRef":{"name":"shop-secret","key":"k"}}}]}]}}}}
            ---
            {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"shop-cfg","namespace":"usage-demo"},"data":{"a":"1"}}
            ---
            {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"keyed-cfg","namespace":"usage-demo"},"data":{"read":"1","never":"2"}}
            ---
            {"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"keyed","namespace":"usage-demo","uid":"u-keyed"},"spec":{"replicas":1,"template":{"spec":{"serviceAccountName":"shop-sa","containers":[{"name":"c","image":"x:1","env":[{"name":"R","valueFrom":{"configMapKeyRef":{"name":"keyed-cfg","key":"read"}}}]}]}}}}
            ---
            {"apiVersion":"autoscaling/v2","kind":"HorizontalPodAutoscaler","metadata":{"name":"ghost-hpa","namespace":"usage-demo"},"spec":{"scaleTargetRef":{"kind":"Deployment","name":"vanished"}}}
            ---
            {"apiVersion":"policy/v1","kind":"PodDisruptionBudget","metadata":{"name":"empty-pdb","namespace":"usage-demo"},"spec":{"minAvailable":1,"selector":{"matchLabels":{"app":"nobody"}}}}
            ---
            {"apiVersion":"v1","kind":"ServiceAccount","metadata":{"name":"shop-sa","namespace":"usage-demo"}}
            ---
            {"apiVersion":"v1","kind":"ServiceAccount","metadata":{"name":"idle-sa","namespace":"usage-demo"}}
            ---
            {"apiVersion":"networking.k8s.io/v1","kind":"IngressClass","metadata":{"name":"usage-unused-class"}}
            ---
            {"apiVersion":"networking.k8s.io/v1","kind":"Ingress","metadata":{"name":"shop","namespace":"usage-demo"},"spec":{"ingressClassName":"usage-missing-class","rules":[{"http":{"paths":[{"backend":{"service":{"name":"usage-no-service"}}}]}}]}}
            ---
            {"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"forgotten-cfg","namespace":"usage-demo"}}
            ---
            {"apiVersion":"v1","kind":"Secret","type":"Opaque","metadata":{"name":"shop-secret","namespace":"usage-demo"},"data":{"k":"c2VjcmV0LXZhbHVl"}}
            ---
            {"apiVersion":"v1","kind":"Secret","type":"Opaque","metadata":{"name":"forgotten-secret","namespace":"usage-demo"},"data":{"k":"c2VjcmV0LXZhbHVl"}}
            ---
            {"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"name":"shop-data","namespace":"usage-demo"},"spec":{"storageClassName":"usage-fast"},"status":{"phase":"Bound"}}
            ---
            {"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"name":"old-data","namespace":"usage-demo"},"spec":{"storageClassName":"usage-fast"},"status":{"phase":"Bound"}}
            ---
            {"apiVersion":"v1","kind":"PersistentVolume","metadata":{"name":"usage-pv-released"},"spec":{"storageClassName":"usage-fast","persistentVolumeReclaimPolicy":"Retain","claimRef":{"namespace":"usage-demo","name":"gone"}},"status":{"phase":"Released"}}
            ---
            {"apiVersion":"storage.k8s.io/v1","kind":"StorageClass","metadata":{"name":"usage-fast"}}
            ---
            {"apiVersion":"storage.k8s.io/v1","kind":"StorageClass","metadata":{"name":"usage-slow"}}
            """);
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();

        ws.Navigate(ws.FindNav(NavTarget.ConfigUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Rows.Count > 0, what: "config usage");
        var vm = (UsageViewModel)ws.CurrentContent!;
        Assert.Equal(UsageStatus.Used, vm.Rows.Single(r => r.Row.Name == "shop-cfg").Row.Status);
        Assert.Equal(UsageStatus.Unused, vm.Rows.Single(r => r.Row.Name == "forgotten-cfg").Row.Status);
        h.Screenshot("40-usage-config");
        vm.UnusedOnly = true;
        Assert.All(vm.Rows, r => Assert.Equal(UsageStatus.Unused, r.Row.Status));
        Assert.Contains(vm.Rows, r => r.Row.Name == "forgotten-cfg");
        vm.UnusedOnly = false;
        vm.Section = 1;
        Assert.Equal(UsageStatus.Unused, vm.Rows.Single(r => r.Row.Name == "forgotten-secret").Row.Status);
        Assert.Equal("Deployment/usage-demo/shop", vm.Rows.Single(r => r.Row.Name == "shop-secret").Row.Consumers.Single().Display);
        vm.Section = 2;
        var dead = Assert.Single(vm.DeadKeyRows);
        Assert.Equal(("usage-demo/keyed-cfg", "never"), (dead.ConfigMap, dead.Key));
        h.Screenshot("42-usage-unread-keys");
        vm.Section = 3;
        Assert.Contains(vm.DanglingRows, r => r.Display == "HorizontalPodAutoscaler/usage-demo/ghost-hpa" && r.Problem.Contains("does not exist"));
        Assert.Contains(vm.DanglingRows, r => r.Display == "PodDisruptionBudget/usage-demo/empty-pdb" && r.Problem.Contains("Selects no pod"));
        h.Screenshot("44-usage-hpa-pdb");
        vm.Section = 4;
        Assert.Contains(vm.BrokenRows, b => b.Target == "ConfigMap/usage-demo/deleted-cfg" && b.Consumer == "Deployment/usage-demo/shop");

        ws.Navigate(ws.FindNav(NavTarget.StorageUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Mode == UsageMode.Storage && v.Rows.Count > 0, what: "storage usage");
        var st = (UsageViewModel)ws.CurrentContent!;
        Assert.Equal(UsageStatus.Used, st.Rows.Single(r => r.Row.Name == "shop-data").Row.Status);
        Assert.Equal(UsageStatus.Unused, st.Rows.Single(r => r.Row.Name == "old-data").Row.Status);
        h.Screenshot("41-usage-storage");
        st.Section = 1;
        Assert.Equal(UsageStatus.Unused, st.Rows.Single(r => r.Row.Name == "usage-pv-released").Row.Status);
        st.Section = 2;
        Assert.Equal(UsageStatus.Used, st.Rows.Single(r => r.Row.Name == "usage-fast").Row.Status);
        Assert.Equal(UsageStatus.Unused, st.Rows.Single(r => r.Row.Name == "usage-slow").Row.Status);
        ws.Navigate(ws.FindNav(NavTarget.AccessUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Mode == UsageMode.Access && v.Rows.Count > 0, what: "access usage");
        var ac = (UsageViewModel)ws.CurrentContent!;
        Assert.Equal(UsageStatus.Used, ac.Rows.Single(r => r.Row.Name == "shop-sa").Row.Status);
        Assert.Equal(UsageStatus.Unused, ac.Rows.Single(r => r.Row.Name == "idle-sa").Row.Status);
        h.Screenshot("43-usage-service-accounts");

        ws.Navigate(ws.FindNav(NavTarget.NetworkUsage)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is UsageViewModel v && v.Mode == UsageMode.Network && v.Rows.Count > 0, what: "network usage");
        var nw = (UsageViewModel)ws.CurrentContent!;
        Assert.Equal(UsageStatus.Unused, nw.Rows.Single(r => r.Row.Name == "usage-unused-class").Row.Status);
        nw.Section = 1;
        Assert.Contains(nw.BrokenRows, b => b.Target == "IngressClass/usage-missing-class");
        Assert.Contains(nw.BrokenRows, b => b.Target == "Service/usage-demo/usage-no-service");
        Assert.DoesNotContain("c2VjcmV0LXZhbHVl", string.Join("|", vm.Rows.Select(r => r.UsedBy + r.Note)));
    }

    [AvaloniaFact]
    public async Task Security_page_audits_workloads_rbac_and_certificates()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Security)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is SecurityViewModel v && v.AuditRows.Count > 0, what: "audit");
        var vm = (SecurityViewModel)ws.CurrentContent!;
        Assert.Contains(vm.AuditRows, r => r.Finding is { Rule: "privileged", Name: "worker", Severity: Core.Security.FindingSeverity.High });
        Assert.Contains(vm.AuditRows, r => r.Finding.Rule == "hostpath" && r.Title.Contains("docker.sock"));
        Assert.Contains(vm.AuditRows, r => r.Finding.Rule == "capability" && r.Title.Contains("SYS_ADMIN"));
        Assert.DoesNotContain(vm.AuditRows, r => r.Finding.Name == "checkout");   // hardened: no finding
        Assert.Equal(vm.AuditRows.Select(r => r.Finding.Severity).OrderByDescending(x => x), vm.AuditRows.Select(r => r.Finding.Severity));
        h.Screenshot("37-security-workloads");
        vm.HighOnly = true;
        Assert.All(vm.AuditRows, r => Assert.Equal(Core.Security.FindingSeverity.High, r.Finding.Severity));
        vm.HighOnly = false;

        vm.Tab = SecurityViewModel.AccessTab;
        await UiHarness.WaitFor(() => vm.AccessRows.Count > 0, what: "rbac findings");
        Assert.Contains(vm.AccessRows, r => r.Finding is { Rule: "cluster-admin-binding", Name: "ops-admin" });
        Assert.Contains(vm.AccessRows, r => r.Finding is { Rule: "read-secrets", Name: "secret-reader" });
        Assert.Contains(vm.AccessRows, r => r.Finding is { Rule: "authenticated-binding", Name: "everyone-views", Severity: Core.Security.FindingSeverity.Medium });
        vm.SubjectQuery = "ServiceAccount default/ci-bot";
        await vm.LookupCommand.ExecuteAsync(null);
        Assert.Contains(vm.SubjectRules, g => g is { Role: "deployer", Verbs: "get, list, patch", Scope: "default" });
        Assert.Contains(vm.SubjectRules, g => g.Binding == "everyone-views");   // through system:authenticated
        h.Screenshot("38-security-access");

        vm.Tab = SecurityViewModel.CertificatesTab;
        await UiHarness.WaitFor(() => vm.Certificates.Count > 2, what: "certificates");
        var expired = vm.Certificates.First();
        Assert.Equal("default/api-tls", expired.Display); Assert.Equal(Core.Security.CertificateHealth.Expired, expired.Info.Health);
        Assert.Contains(vm.Certificates, c => c.Display == "default/legacy-tls" && c.Info.Health == Core.Security.CertificateHealth.ExpiresSoon);
        Assert.DoesNotContain(vm.Certificates, c => c.Info.Source == "TLS secret" && c.Display == "default/web-tls-secret");   // shown through its Certificate
        Assert.Contains(vm.Certificates, c => c.Display == "production/shop-tls" && c.Info.Health == Core.Security.CertificateHealth.Ok);
        Assert.DoesNotContain("demo-not-a-real-key", string.Join("|", vm.Certificates.Select(c => c.Info.ToString())));
        h.Screenshot("39-security-certificates");
    }

    [AvaloniaFact]
    public async Task GitOps_page_lists_argo_and_flux_and_acts_through_cluster_actions()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Navigate(ws.FindNav(NavTarget.GitOps)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is GitOpsViewModel v && v.Rows.Count == 10, what: "gitops rows");
        var vm = (GitOpsViewModel)ws.CurrentContent!;
        Assert.Equal(5, vm.Rows.Count(r => r.App.Tool == Core.Delivery.GitOpsTool.ArgoCd));
        Assert.True(vm.Rows.Take(4).All(r => r.App.IsProblem));   // what needs attention first
        h.Screenshot("40-gitops");

        vm.Selected = vm.Rows.First(r => r.Display == "argocd/payments");
        Assert.Equal("Degraded", vm.Selected.Health);
        Assert.Equal(["ConfigMap/payments/api-config", "Deployment/payments/api", "Service/payments/api"], vm.Resources.Select(r => r.Display));   // out-of-sync ones first
        Assert.True(vm.Resources[0].IsProblem && vm.Resources[1].IsProblem && !vm.Resources[2].IsProblem);
        await vm.Actions.First(a => a.Header == "Hard refresh from Git").ExecuteAsync();
        var apps = new Core.Api.Gvr("argoproj.io", "v1alpha1", "applications");
        Assert.Equal("hard", h.Demo.Server.Get(apps, "argocd", "payments")!["metadata"]!["annotations"]!["argocd.argoproj.io/refresh"]!.ToString());
        await vm.Actions.First(a => a.Header == "Sync").ExecuteAsync();
        Assert.Equal("kubism", h.Demo.Server.Get(apps, "argocd", "payments")!["operation"]!["initiatedBy"]!["username"]!.ToString());

        vm.ProblemsOnly = true;
        Assert.All(vm.Rows, r => Assert.True(r.App.IsProblem));
        vm.ProblemsOnly = false;
        vm.Filter = "podinfo";
        var podinfo = Assert.Single(vm.Rows);
        Assert.True(podinfo.App.Suspended);
        vm.Selected = podinfo;
        var hr = new Core.Api.Gvr("helm.toolkit.fluxcd.io", "v2", "helmreleases");
        await vm.Actions.First(a => a.Header == "Resume").ExecuteAsync();
        Assert.Null(h.Demo.Server.Get(hr, "flux-system", "podinfo")!["spec"]!["suspend"]);
        Assert.Contains(ws.Session.Activity.Entries, e => e.Action == "FluxResume" && e.Success);
    }

    [AvaloniaFact]
    public async Task GitOps_actions_follow_the_safety_mode()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-production");
        ws.Navigate(ws.FindNav(NavTarget.GitOps)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is GitOpsViewModel v && v.Rows.Count == 10, what: "gitops rows");
        var vm = (GitOpsViewModel)ws.CurrentContent!;
        var apps = new Core.Api.Gvr("argoproj.io", "v1alpha1", "applications");
        vm.Selected = vm.Rows.First(r => r.Display == "argocd/legacy");
        // Pruning on production asks for the name; a wrong one is refused and nothing is changed.
        h.Dialogs.TypedResponse = _ => "wrong";
        await vm.Actions.First(a => a.Header == "Sync and prune…").ExecuteAsync();
        Assert.Null(h.Demo.Server.Get(apps, "argocd", "legacy")!["operation"]);
        h.Dialogs.TypedResponse = e => e;
        await vm.Actions.First(a => a.Header == "Sync and prune…").ExecuteAsync();
        Assert.True(h.Demo.Server.Get(apps, "argocd", "legacy")!["operation"]!["sync"]!["prune"]!.GetValue<bool>());
    }

    [AvaloniaFact]
    public async Task Capacity_page_shows_nodes_pending_pods_cost_and_the_right_sizing_what_if()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Navigate(ws.FindNav(NavTarget.Capacity)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is CapacityViewModel v && v.Nodes.Count == 2 && v.Pending.Count > 0, what: "capacity");
        var vm = (CapacityViewModel)ws.CurrentContent!;
        Assert.Contains("2 nodes", vm.Summary);
        Assert.Contains("fragmentation", vm.Fragmentation);
        var big = vm.Pending.Single(p => p.Pod.Name == "report-batch-0");
        Assert.StartsWith("Fragmented", big.Verdict);   // the free CPU is split over the two nodes
        Assert.Contains("Insufficient cpu", big.Reason);
        Assert.All(vm.Nodes, n => Assert.True(n.Node.CpuRequested > 0));
        h.Screenshot("41-capacity-nodes");

        vm.Tab = CapacityViewModel.CostTab;
        Assert.True(vm.NeedsPrices);
        Assert.Contains("Set a price", vm.CostSummary);
        vm.PriceCpu = "0.03"; vm.PriceMemory = "0.004";
        vm.ApplyPricesCommand.Execute(null);
        Assert.False(vm.NeedsPrices);
        Assert.Equal(0.03, ws.Session.Metadata.PricePerVcpuHour);   // saved with the cluster's settings
        Assert.Contains("idle", vm.CostSummary);
        Assert.Contains(vm.CostRows, r => r.Key == "production" && r.Line.MonthlyCost > 0);
        Assert.Equal(vm.CostRows.Select(r => r.Line.MonthlyCost), vm.CostRows.Select(r => r.Line.MonthlyCost).OrderByDescending(x => x));
        vm.GroupBy = "Label"; vm.LabelKey = "app";
        Assert.Contains(vm.CostRows, r => r.Key == "web");
        h.Screenshot("42-capacity-cost");

        vm.Tab = CapacityViewModel.WhatIfTab;
        await UiHarness.WaitFor(() => vm.SimRows.Count > 0, what: "simulation");
        Assert.Contains("Prometheus", vm.SimSource);
        Assert.Contains("Requests would go from", vm.SimSummary);
        Assert.Contains(vm.SimRows, r => r.Namespace == "default" && r.Ns.CpuAfter < r.Ns.CpuNow);          // web is over-provisioned
        Assert.Contains(vm.SimRows, r => r.Namespace == "production" && r.Ns.CpuAfter > r.Ns.CpuNow);       // checkout is under-provisioned: it costs more
        h.Screenshot("43-capacity-what-if");
    }

    private static async Task<(UiHarness h, WorkspaceViewModel ws, HelmViewModel vm)> OpenHelmAsync(string context, string release)
    {
        var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync(context);
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Releases.Any(r => r.Name == release), what: "releases");
        var vm = (HelmViewModel)ws.CurrentContent!;
        vm.Selected = vm.Releases.First(r => r.Name == release);
        return (h, ws, vm);
    }

    [AvaloniaFact]
    public async Task Edit_values_starts_from_every_value_and_upgrade_is_offered_once_something_differs()
    {
        var (h, ws, vm) = await OpenHelmAsync("demo-staging", "api");
        await using var _ = h;
        vm.EditValuesCommand.Execute(null);
        Assert.Equal(HelmViewModel.UpgradeTabIndex, vm.Tab);
        await UiHarness.WaitFor(() => vm.CanRebuild && vm.UseInstalledChart, what: "installed chart read");
        Assert.Equal("replicaCount: 2", vm.UpgradeValues.Trim());   // like Lens: the existing values, not an empty example
        Assert.False(vm.ValuesChanged); Assert.False(vm.CanUpgrade);
        Assert.Contains("Same as the installed", vm.ValuesState);

        vm.UpgradeValues += "logLevel: debug\n";
        Assert.True(vm.ValuesChanged); Assert.True(vm.CanUpgrade);
        Assert.Contains("Modified", vm.ValuesState);
        vm.ResetValuesCommand.Execute(null);
        Assert.False(vm.CanUpgrade);                                  // back to the installed values: nothing to upgrade
        vm.UpgradeValues = "replicaCount:   2   # same value, other spelling\n";
        Assert.False(vm.ValuesChanged);                               // spelling and comments are not changes
        vm.UpgradeValues = "replicaCount: [unclosed\n";
        Assert.False(vm.CanUpgrade); Assert.NotNull(vm.ValuesError);

        h.Helm.Output["template"] = "---\n# Source: api/templates/settings.yaml\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: api-settings\ndata:\n  replicaCount: \"2\"\n  logLevel: \"debug\"\n";
        vm.UpgradeValues = "replicaCount: 2\nlogLevel: debug\n";
        Assert.True(vm.CanUpgrade); Assert.Null(vm.ValuesError);
        await vm.PreviewUpgradeCommand.ExecuteAsync(null);
        Assert.Null(vm.UpgradeError);
        Assert.Contains(vm.PreviewLines, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("info"));
        Assert.Contains(vm.PreviewLines, l => l.Kind == DiffLineKind.Added && l.Text.Contains("debug"));
        Assert.Contains("would change", vm.UpgradeStatus);
        Assert.Equal("template", h.Helm.Calls[^1][0]);
        Assert.Equal("logLevel: debug\n", h.Helm.Values[^1]);        // only the difference from the chart's defaults is sent
        h.Screenshot("44-helm-edit-values");

        vm.Atomic = true;
        await vm.UpgradeCommand.ExecuteAsync(null);
        var upgrade = h.Helm.Calls.Last(c => c[0] == "upgrade");
        Assert.Equal(["upgrade", "api"], upgrade[..2]);
        Assert.Contains("--atomic", upgrade); Assert.Contains("--reset-values", upgrade);
        Assert.Equal("logLevel: debug\n", h.Helm.Values[^1]);
        Assert.Contains(ws.Session.Activity.Entries, e => e.Action == "HelmUpgrade" && e.Success);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("confirm:Upgrade"));
        await UiHarness.WaitFor(() => vm.Selected?.Name == "api" && !vm.ValuesChanged, what: "release reloaded");
    }

    [AvaloniaFact]
    public async Task Removing_a_default_value_is_sent_as_null_and_helm_errors_are_reported()
    {
        var (h, _, vm) = await OpenHelmAsync("demo-staging", "api");
        await using var _ = h;
        vm.EditValuesCommand.Execute(null);
        await UiHarness.WaitFor(() => vm.CanRebuild, what: "installed chart read");
        h.Helm.Output["template"] = "---\n# Source: api/templates/settings.yaml\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: api-settings\ndata:\n  replicaCount: \"2\"\n  logLevel: \"info\"\n";
        vm.UpgradeValues = "logLevel: warn\n";                        // replicaCount removed: the default (2) comes from the chart
        await vm.PreviewUpgradeCommand.ExecuteAsync(null);
        Assert.Equal("logLevel: warn\nreplicaCount: \n", h.Helm.Values[^1].Replace("null", ""));
        Assert.Contains("No change", vm.UpgradeStatus.Length > 0 ? vm.UpgradeStatus : "No change");

        h.Helm.FailWith = "helm failed (exit 1): template: api/templates/settings.yaml: bad value";
        await vm.PreviewUpgradeCommand.ExecuteAsync(null);
        Assert.Contains("bad value", vm.UpgradeError);                 // helm's own message
    }

    [AvaloniaFact]
    public async Task A_chart_with_no_stored_templates_needs_a_chart_reference()
    {
        var (h, _, vm) = await OpenHelmAsync("demo-staging", "ingress-nginx");
        await using var _ = h;
        h.Helm.Output["search"] = "[{\"name\":\"other/nginx\",\"version\":\"1\"},{\"name\":\"ingress-nginx/ingress-nginx\",\"version\":\"4.11.1\"}]";
        vm.EditValuesCommand.Execute(null);
        await UiHarness.WaitFor(() => vm.RebuildBlockedReason is not null && vm.ChartRef.Length > 0, what: "chart suggestion");
        Assert.False(vm.CanRebuild); Assert.False(vm.UseInstalledChart); Assert.True(vm.UseReference);
        Assert.Equal("ingress-nginx/ingress-nginx", vm.ChartRef);   // found by name in the repositories helm knows
        Assert.Equal("4.11.0", vm.ChartVersion);                       // the installed version, until you change it
        Assert.True(vm.CanUpgrade);                                    // a chart is given, so there is something to do even with the same values
        vm.UseInstalledChart = true;
        await vm.UpgradeCommand.ExecuteAsync(null);
        Assert.Contains("templates", vm.UpgradeError);
        vm.UseReference = true; vm.ChartRef = ""; await vm.UpgradeCommand.ExecuteAsync(null);
        Assert.Contains("chart reference", vm.UpgradeError);
        vm.ChartRef = "ingress-nginx/ingress-nginx"; vm.ChartVersion = "4.11.2";
        await vm.UpgradeCommand.ExecuteAsync(null);
        var up = h.Helm.Calls.Last(c => c[0] == "upgrade");
        Assert.Equal("ingress-nginx/ingress-nginx", up[2]); Assert.Contains("4.11.2", up);
    }

    [AvaloniaFact]
    public async Task After_an_uninstall_the_list_is_read_again_and_the_release_is_gone()
    {
        var (h, _, vm) = await OpenHelmAsync("demo-staging", "api");
        await using var _ = h;
        h.Helm.OnRun = args =>
        {
            if (args[0] == "uninstall") h.Demo.Server.Remove(Core.Api.Gvr.Secrets, "default", "sh.helm.release.v1.api.v1");
        };
        await vm.UninstallCommand.ExecuteAsync(null);
        await UiHarness.WaitFor(() => vm.Releases.All(r => r.Name != "api"), what: "list refreshed");
        Assert.NotEqual("api", vm.Selected?.Name);
        Assert.Contains(h.Helm.Calls, c => c[0] == "uninstall");
    }

    [AvaloniaFact]
    public async Task After_a_rollback_the_releases_are_read_again_and_the_same_release_stays_selected()
    {
        var (h, _, vm) = await OpenHelmAsync("demo-staging", "ingress-nginx");
        await using var _ = h;
        await UiHarness.WaitFor(() => vm.History.Count == 3, what: "history");
        int ReleaseListings() => h.Demo.Server.Requests.Count(r => r.Method == "GET" && r.Path.EndsWith("/secrets") && Uri.UnescapeDataString(r.Query).Contains("owner=helm"));
        var before = ReleaseListings();
        await vm.RollbackCommand.ExecuteAsync(vm.History.First(r => r.Revision == 1));
        await UiHarness.WaitFor(() => ReleaseListings() > before && vm.Selected?.Name == "ingress-nginx", what: "releases read again");
        Assert.Contains(h.Helm.Calls, c => c[0] == "rollback" && c[1] == "ingress-nginx" && c[2] == "1");
    }

    [AvaloniaFact]
    public async Task Every_page_refreshes_with_the_same_icon_button_and_a_tooltip()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        foreach (var target in new[] { NavTarget.Helm, NavTarget.GitOps, NavTarget.Security, NavTarget.Capacity, NavTarget.RightSizing })
        {
            ws.Navigate(ws.FindNav(target)!);
            await UiHarness.WaitFor(() => ws.CurrentContent is not null && h.Find<Kubism.Desktop.Controls.RefreshButton>(b => b.IsEffectivelyVisible) is not null, what: $"{target} refresh button");
            var button = h.Find<Kubism.Desktop.Controls.RefreshButton>(b => b.IsEffectivelyVisible)!;
            Assert.StartsWith("Refresh", Avalonia.Controls.ToolTip.GetTip(button)?.ToString());
            Assert.IsNotType<string>(button.Content);                          // an icon, not the word
            var words = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(h.Window).OfType<Avalonia.Controls.Button>().Where(b => b.IsEffectivelyVisible && b.Content is string text && text is "Refresh" or "Refresh diagnosis" or "Reload").Select(b => (string)b.Content!).ToList();
            Assert.True(words.Count == 0, $"{target}: text refresh buttons left: {string.Join(", ", words)}");
        }
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel, what: "pods");
        Assert.NotNull(h.Find<Kubism.Desktop.Controls.RefreshButton>(b => b.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public async Task Upgrade_on_production_needs_the_release_name_typed()
    {
        var (h, _, vm) = await OpenHelmAsync("demo-production", "api");
        await using var _ = h;
        vm.EditValuesCommand.Execute(null);
        await UiHarness.WaitFor(() => vm.CanRebuild, what: "installed chart read");
        vm.UpgradeValues += "logLevel: debug\n";
        h.Dialogs.TypedResponse = _ => "wrong";
        await vm.UpgradeCommand.ExecuteAsync(null);
        Assert.DoesNotContain(h.Helm.Calls, c => c[0] == "upgrade");
        h.Dialogs.TypedResponse = e => e;
        await vm.UpgradeCommand.ExecuteAsync(null);
        Assert.Contains(h.Helm.Calls, c => c[0] == "upgrade");
    }
}
