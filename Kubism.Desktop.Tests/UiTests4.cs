using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.RightSizing;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiRightSizingAndHelmTests
{
    private static async Task<(UiHarness h, WorkspaceViewModel ws, RightSizingViewModel vm)> OpenAsync(string ctx = "demo-staging")
    {
        var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync(ctx);
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.RightSizing)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RightSizingViewModel, what: "right-sizing vm");
        var vm = (RightSizingViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => !vm.IsLoading && (vm.Rows.Count > 0 || vm.Error is not null), what: "right-sizing load");
        return (h, ws, vm);
    }

    [AvaloniaFact]
    public async Task Dashboard_shows_verdicts_waste_and_recommendations_from_prometheus()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        Assert.Null(vm.Error);
        Assert.Contains("Prometheus", vm.SourceInfo);
        Assert.Contains("7 d", vm.SourceInfo);
        var web = vm.Rows.Where(r => r.Workload == "Deployment/web").ToList();
        Assert.Single(web);
        Assert.Equal("Over-provisioned", web[0].Verdict);
        Assert.NotEqual("—", web[0].RecCpu);
        var checkout = vm.Rows.Single(r => r.Workload == "Deployment/checkout");
        Assert.Equal("Under-provisioned", checkout.Verdict);   // p95 > request and throttled in the fake series
        var crashy = vm.Rows.Single(r => r.Workload == "Pod/crashy-0");
        Assert.Equal("No request", crashy.Verdict);
        Assert.True(vm.UnderCount >= 1 && vm.NoRequestCount >= 1);
        Assert.NotEmpty(vm.Rollups);
        Assert.Contains("requested", vm.CpuKpi);
        h.Screenshot("17-rightsizing");
    }

    [AvaloniaFact]
    public async Task Filters_sorting_and_window_selector()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        vm.VerdictFilter = "Over-provisioned";
        Assert.All(vm.Rows, r => Assert.Equal("Over-provisioned", r.Verdict));
        vm.VerdictFilter = "All"; vm.KindFilter = "checkout";
        Assert.All(vm.Rows, r => Assert.Contains("checkout", r.Workload));
        vm.KindFilter = ""; vm.SortBy = "Waste";
        Assert.True(vm.Rows[0].Report.WasteCpuCores >= vm.Rows[^1].Report.WasteCpuCores);
        vm.MinWasteCores = 0.1;
        Assert.All(vm.Rows, r => Assert.True(r.Report.WasteCpuCores >= 0.1));
        vm.MinWasteCores = 0;
        var queriesBefore = h.Demo.Prometheus.Queries.Count;
        vm.WindowDays = 14;
        await UiHarness.WaitFor(() => h.Demo.Prometheus.Queries.Count > queriesBefore, what: "requery");
        await UiHarness.WaitFor(() => !vm.IsLoading && vm.SourceInfo.Contains("14 d"));
    }

    [AvaloniaFact]
    public async Task Selecting_containers_gives_a_patch_preview_and_copyable_snippets()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        var web = vm.Rows.Single(r => r.Workload == "Deployment/web");
        web.IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.Contains("-   requests.cpu: 100m", vm.PatchPreview);
        Assert.Contains("+   requests.cpu:", vm.PatchPreview);
        Assert.Null(vm.ApplyBlockedReason);
        await vm.CopyYamlCommand.ExecuteAsync(null);
        Assert.Contains("kind: Deployment", h.Clipboard.Text);
        await vm.CopyHelmCommand.ExecuteAsync(null);
        Assert.Contains("resources:", h.Clipboard.Text);
        await vm.CopyKustomizeCommand.ExecuteAsync(null);
        Assert.Contains("patch: |-", h.Clipboard.Text);
        vm.Selected = web;
        await UiHarness.WaitFor(() => vm.CpuSeries.Count > 10, what: "series");
        h.Screenshot("18-rightsizing-selection");
    }

    [AvaloniaFact]
    public async Task Details_follow_the_clicked_container_including_one_without_a_recommendation()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        var web = vm.Rows.Single(r => r.Workload == "Deployment/web");
        vm.Selected = web;
        Assert.Contains("Deployment/web", vm.PatchPreview);
        Assert.Contains("requests.cpu", vm.PatchPreview);

        // another container: the preview is its own, not the previous one's
        // picked from the "Misconfigured" list, the grid on screen (a row it does not list would clear the shared selection)
        var other = vm.Problems.First(r => r != web && r.Report.Recommended is not null);
        vm.Selected = other;
        Assert.Contains($"{other.Report.WorkloadKind}/{other.Report.WorkloadName}", vm.PatchPreview);
        Assert.DoesNotContain("Deployment/web", vm.PatchPreview);

        // a container with no recommendation says so
        var none = vm.Problems.FirstOrDefault(r => r.Report.Recommended is null);
        if (none is not null)
        {
            vm.Selected = none;
            Assert.Contains("No recommendation", vm.PatchPreview);
            Assert.Contains(none.Report.WorkloadName, vm.PatchPreview);
        }

        // ticked containers take over the preview
        web.IsSelected = true;
        Assert.Contains("Deployment/web", vm.PatchPreview);
        vm.Selected = null;
        web.IsSelected = false;
        Assert.Equal("", vm.PatchPreview);
    }

    [AvaloniaFact]
    public async Task Apply_patches_the_workload_after_confirmation_and_triggers_reload()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        var web = vm.Rows.Single(r => r.Workload == "Deployment/web");
        var rec = web.Report.Recommended!;
        web.IsSelected = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("confirm:Apply right-sizing") && s.Contains("rollout"));
        var res = h.Demo.Server.Get(Gvr.Deployments, "default", "web")!["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!;
        Assert.Equal(Core.Views.Quantities.FormatCpu(rec.CpuRequest), res["requests"]!["cpu"]!.GetValue<string>());
        Assert.Contains(ws.Session.Activity.Entries, e => e.Action == "SetResources" && e.Success);
    }

    [AvaloniaFact]
    public async Task Gitops_managed_workload_refuses_direct_apply_and_offers_the_snippet()
    {
        await using var h = await UiHarness.StartAsync();
        var dep = h.Demo.Server.Get(Gvr.Deployments, "default", "web")!;
        dep["metadata"]!["labels"]!["argocd.argoproj.io/instance"] = "web-app";
        h.Demo.Server.Upsert(dep);
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.RightSizing)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RightSizingViewModel);
        var vm = (RightSizingViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.Rows.Count > 0);
        var web = vm.Rows.Single(r => r.Workload == "Deployment/web");
        Assert.Equal("Argo CD", web.GitOps);
        web.IsSelected = true;
        Assert.Contains("reverted by Argo CD", vm.ApplyBlockedReason);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("error:Cannot apply"));
        Assert.Null(h.Demo.Server.Get(Gvr.Deployments, "default", "web")!["spec"]!["template"]!["spec"]!["containers"]![0]!["resources"]!["limits"]);
        vm.OverrideGitOps = true;
        Assert.Null(vm.ApplyBlockedReason);
    }

    [AvaloniaFact]
    public async Task Production_apply_needs_typed_workload_name_and_read_only_blocks()
    {
        var (h, ws, vm) = await OpenAsync("demo-production");
        await using var _ = h;
        var checkout = vm.Rows.Single(r => r.Workload == "Deployment/checkout");
        checkout.IsSelected = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("typed:") && s.EndsWith("checkout"));
        var m = h.Main.Settings.GetMetadata(ws.Id); m.SafetyMode = Core.Catalog.SafetyMode.ReadOnly; h.Main.Settings.SetMetadata(ws.Id, m);
        await UiHarness.WaitFor(() => !vm.IsLoading, what: "reload after apply");
        checkout = vm.Rows.Single(r => r.Workload == "Deployment/checkout");
        checkout.IsSelected = true;
        Assert.Contains("read-only", vm.ApplyBlockedReason);
    }

    [AvaloniaFact]
    public async Task Export_writes_csv_and_markdown_and_create_vpa_goes_through_actions()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        h.Dialogs.SavePath = Path.Combine(h.DataDir, "report.csv");
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.StartsWith("cluster_source,namespace", File.ReadAllText(h.Dialogs.SavePath));
        h.Dialogs.SavePath = Path.Combine(h.DataDir, "report.md");
        await vm.ExportMarkdownCommand.ExecuteAsync(null);
        var md = File.ReadAllText(h.Dialogs.SavePath);
        Assert.Contains("# Right-sizing report", md); Assert.Contains("Strategy:", md); Assert.Contains("Window: 7 days", md);
        vm.Rows.Single(r => r.Workload == "Deployment/web").IsSelected = true;
        await vm.CreateVpaCommand.ExecuteAsync(null);
        Assert.NotNull(h.Demo.Server.Get(new Gvr("autoscaling.k8s.io", "v1", "verticalpodautoscalers"), "default", "web"));
    }

    [AvaloniaFact]
    public async Task Unreachable_prometheus_reports_the_error_and_test_connection_works()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        await h.Demo.Prometheus.DisposeAsync();
        ws.Navigate(ws.FindNav(NavTarget.RightSizing)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RightSizingViewModel);
        var vm = (RightSizingViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.Error is not null, what: "error");
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.ConnectionResult));
        // Falling back to VPA source works without Prometheus.
        vm.SelectedSource = vm.Sources.First(s => s.Name == "VPA recommender");
        await UiHarness.WaitFor(() => !vm.IsLoading && vm.Error is null, what: "vpa source");
    }

    [AvaloniaFact]
    public async Task Helm_view_lists_releases_with_values_and_history()
    {
        await using var h = await UiHarness.StartAsync();
        foreach (var rev in new[] { 1, 2 })
            h.Demo.Server.Upsert(new JsonObject
            {
                ["apiVersion"] = "v1", ["kind"] = "Secret",
                ["metadata"] = new JsonObject { ["name"] = $"sh.helm.release.v1.nginx.v{rev}", ["namespace"] = "default", ["labels"] = new JsonObject { ["owner"] = "helm", ["name"] = "nginx" } },
                ["type"] = "helm.sh/release.v1",
                ["data"] = new JsonObject { ["release"] = HelmCompat.Encode("nginx", rev) },
            });
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel);
        var vm = (HelmViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.Releases.Any(r => r.Name == "nginx"), what: "releases");
        vm.Selected = vm.Releases.First(r => r.Name == "nginx");
        Assert.Equal(2, vm.Selected.Revision);
        Assert.Contains("replicas: 3", vm.ValuesText);
        await UiHarness.WaitFor(() => vm.History.Count == 2, what: "history");
        Assert.Equal([2, 1], vm.History.Select(r => r.Revision));
        Assert.Contains("kind: Deployment", vm.ManifestText);
        Assert.False(vm.CanWrite == false && vm.WriteTooltip.Length == 0);
        h.Screenshot("19-helm");
    }
}

public class UiHelmDiagnosisTests
{
    private static JsonObject ReleaseSecret(string name, int rev, string status, string description, DateTimeOffset updated) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "Secret",
        ["metadata"] = new JsonObject { ["name"] = $"sh.helm.release.v1.{name}.v{rev}", ["namespace"] = "default", ["labels"] = new JsonObject { ["owner"] = "helm", ["name"] = name } },
        ["type"] = "helm.sh/release.v1",
        ["data"] = new JsonObject { ["release"] = HelmCompat.Encode(name, rev, status, description, updated) },
    };

    [AvaloniaFact]
    public async Task Failed_release_shows_events_and_crashed_container_logs_at_a_glance()
    {
        await using var h = await UiHarness.StartAsync();
        var crashy = h.Demo.Server.Get(Gvr.Pods, "default", "crashy-0")!;
        crashy["metadata"]!["labels"]!["app.kubernetes.io/instance"] = "broken";
        h.Demo.Server.Upsert(crashy);
        var ev = h.Demo.Server.Get(Gvr.Events, "default", "crashy-0.1")!;
        ev["lastTimestamp"] = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O");
        h.Demo.Server.Upsert(ev);
        h.Demo.Server.Upsert(ReleaseSecret("broken", 1, "failed", "Helm upgrade failed: timed out waiting for the condition", DateTimeOffset.UtcNow.AddMinutes(-10)));
        h.Demo.Server.Upsert(ReleaseSecret("fine", 1, "deployed", "Install complete", DateTimeOffset.UtcNow.AddHours(-3)));
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Releases.Any(r => r.Name == "fine"));
        var vm = (HelmViewModel)ws.CurrentContent!;
        vm.Selected = vm.Releases.First(r => r.Name == "broken");
        await UiHarness.WaitFor(() => vm.Selected?.Name == "broken" && !vm.DiagLoading && vm.DiagPods.Count > 0, what: "diagnosis");
        Assert.True(vm.HasFailing);
        Assert.Contains("broken", vm.FailingSummary);
        Assert.Contains("payments", vm.FailingSummary); // the demo's own failed release
        Assert.Equal(0, vm.Tab);
        Assert.Contains("timed out", vm.DiagHeadline);
        Assert.Contains(vm.DiagPods, p => p.Name == "crashy-0" && p.Status == "CrashLoopBackOff");
        var logs = vm.DiagPods.First(p => p.Name == "crashy-0").Logs;
        Assert.Contains(logs, l => l.Previous && l.Text.Contains("previous container crashed"));
        Assert.Contains(vm.DiagEvents, e => e.Reason == "BackOff");
        h.Screenshot("24-helm-failed-diagnosis");
        vm.Selected = vm.Releases.First(r => r.Name == "fine");
        await UiHarness.WaitFor(() => !vm.DiagIsProblem);
        Assert.Contains("no problem", vm.DiagHeadline);
        Assert.Empty(vm.DiagPods);
    }

    [Fact]
    public void Pending_release_counts_as_a_timeout_only_after_five_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        Core.Helm.HelmRelease R(string status, int minutes) => new("x", "d", 1, status, "c", "1", null, now.AddMinutes(-minutes), null, null, null, null, null);
        Assert.False(Core.Helm.HelmDiagnostics.IsProblem(R("pending-upgrade", 2), now));
        Assert.True(Core.Helm.HelmDiagnostics.IsProblem(R("pending-upgrade", 9), now));
        Assert.True(Core.Helm.HelmDiagnostics.IsProblem(R("failed", 0), now));
        Assert.False(Core.Helm.HelmDiagnostics.IsProblem(R("deployed", 999), now));
        Assert.Contains("--wait timeout", Core.Helm.HelmDiagnostics.Headline(R("pending-install", 9), now));
    }
}

public class UiDemoHelmTests
{
    [AvaloniaFact]
    public async Task Demo_cluster_has_healthy_failed_and_stuck_helm_releases()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Helm)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is HelmViewModel v && v.Releases.Count == 5, what: "demo releases");
        var vm = (HelmViewModel)ws.CurrentContent!;
        Assert.Equal(["cert-manager", "api", "ingress-nginx", "payments", "reporting"], vm.Releases.Select(r => r.Name));
        Assert.Equal(3, vm.Releases.First(r => r.Name == "ingress-nginx").Revision);
        Assert.Equal(["payments", "reporting"], vm.Failing.Select(r => r.Name).OrderBy(x => x));
        await UiHarness.WaitFor(() => vm.Selected is not null && !vm.DiagLoading && vm.DiagPods.Count > 0);
        Assert.Equal("payments", vm.Selected!.Name);   // first failing one is picked
        Assert.Contains(vm.DiagEvents, e => e.Reason == "BackOff");
        vm.Selected = vm.Releases.First(r => r.Name == "ingress-nginx");
        Assert.Contains("replicaCount: 3", vm.ValuesText);
        await UiHarness.WaitFor(() => vm.History.Count == 3);
        vm.Selected = vm.Releases.First(r => r.Name == "reporting");
        await UiHarness.WaitFor(() => vm.DiagHeadline.Contains("Stuck in pending-upgrade"));
    }
}

public class UiRightSizingOverviewTests
{
    [AvaloniaFact]
    public async Task Top_shows_capacity_usage_requests_limits_then_only_the_misconfigured_containers()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.RightSizing)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RightSizingViewModel v && !v.IsLoading && v.CpuCard is not null && v.Problems.Count > 0, what: "overview");
        var vm = (RightSizingViewModel)ws.CurrentContent!;
        // 1) what is available, 2) used now, 3) requests and limits against it
        Assert.Equal(7.84, vm.CpuCard!.Capacity, 2);            // 2 nodes × 3920m allocatable
        Assert.True(vm.CpuCard.UsedNow > 0);                   // metrics-server sums of the demo pods
        Assert.True(vm.CpuCard.Requested > 0 && vm.CpuCard.Limits > 0);
        Assert.Contains("available", vm.CpuCard.Summary); Assert.Contains("used now", vm.CpuCard.Summary);
        Assert.True(vm.MemoryCard!.Capacity > 0 && vm.MemoryCard.Requested > 0);
        // 4) misconfigured only, worst first, each with a plain sentence and a suggestion
        Assert.All(vm.Problems, p => Assert.True(p.IsProblem));
        Assert.DoesNotContain(vm.Problems, p => p.Verdict == "OK");
        Assert.True(vm.Problems.Count < vm.Rows.Count + 1);
        var first = vm.Problems[0];
        Assert.Equal("Under-provisioned", first.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(first.Problem));
        var noReq = vm.Problems.First(p => p.Workload == "Pod/crashy-0");
        Assert.Contains("no CPU request", noReq.Problem);
        var over = vm.Problems.First(p => p.Workload == "Deployment/web");
        Assert.Contains("CPU request", over.Problem); Assert.Contains("uses", over.Problem);
        Assert.StartsWith("CPU", over.Fix);
        Assert.Contains("misconfigured", vm.ProblemsTitle);
        h.Screenshot("30-rightsizing-overview");
    }
}
