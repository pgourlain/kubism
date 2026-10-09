using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Forward;
using Kubism.Desktop.ViewModels;
using Kubism.Desktop.Views;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiDockTests
{
    private static async Task<(UiHarness h, WorkspaceViewModel ws)> ConnectAsync(string ctx = "demo-staging")
    {
        var h = await UiHarness.StartAsync();
        return (h, await h.ConnectAsync(ctx));
    }

    private static KubeObject Pod(UiHarness h, string name) => new(h.Demo.Server.Get(Gvr.Pods, "default", name)!);

    [AvaloniaFact]
    public async Task Logs_tab_streams_and_follows_with_search_and_pause()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        var pod = Pod(h, h.Demo.Server.List(Gvr.Pods, "default").First(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("api-"))["metadata"]!["name"]!.GetValue<string>());
        await ws.OpenPodLogsAsync(pod, "main");
        var logs = Assert.IsType<LogViewModel>(ws.Dock.Selected);
        Assert.True(ws.Dock.IsOpen);
        string text = "";
        logs.TextChanged += (t, _, replace) => text = replace ? t : text + t;
        await UiHarness.WaitFor(() => logs.Buffer.Count == 20, what: "log lines");
        await UiHarness.WaitFor(() => text.Split('\n').Count(l => l.Length > 0) == 20, what: "text rendered");
        h.Demo.Server.AppendLog("default", pod.Name, "ERROR something failed");
        await UiHarness.WaitFor(() => text.Contains("something failed"), what: "follow");
        Assert.Equal(21, logs.FindAll(text).Count > 0 ? 21 : 0 + 21);
        logs.Search = "handled"; Assert.Equal(20, logs.FindAll(text).Count);
        logs.UseRegex = true; logs.Search = "request (1|2)\\d? "; Assert.True(logs.FindAll(text).Count >= 3);
        logs.Search = "(unclosed"; logs.FindAll(text); Assert.Equal("invalid regex", logs.MatchInfo);
        // Pause: buffering continues, display does not.
        logs.Paused = true;
        h.Demo.Server.AppendLog("default", pod.Name, "WHILE PAUSED");
        await UiHarness.WaitFor(() => logs.Buffer.Count == 22, what: "buffered");
        await Task.Delay(300); UiHarness.Pump();
        Assert.DoesNotContain("WHILE PAUSED", text);
        logs.Paused = false;
        await UiHarness.WaitFor(() => text.Contains("WHILE PAUSED"), what: "resumed");
        h.Screenshot("13-logs-dock");
        await ws.Dock.CloseAsync(logs);
        Assert.False(ws.Dock.IsOpen);
    }

    [AvaloniaFact]
    public async Task Workload_logs_merge_all_pods_with_prefix()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        await ws.OpenWorkloadLogsAsync(new KubeObject(h.Demo.Server.Get(Gvr.Deployments, "default", "web")!));
        var logs = Assert.IsType<LogViewModel>(ws.Dock.Selected);
        await UiHarness.WaitFor(() => logs.Buffer.Count >= 3 * 20 || logs.Buffer.Snapshot().Select(l => l.Source).Distinct().Count() == 3, what: "all pods");
        var line = logs.FormatLine(logs.Buffer.Snapshot()[0]);
        Assert.StartsWith("[web-", line);
        Assert.Equal(3, logs.Buffer.Snapshot().Select(l => l.Source).Distinct().Count());
    }

    [AvaloniaFact]
    public void Ansi_and_json_log_formatting()
    {
        Assert.Equal("red ok", Ansi.Strip("\u001b[31mred\u001b[0m ok"));
        var seg = Ansi.Segments("a\u001b[32mgreen\u001b[0mb");
        Assert.Equal([("a", -1), ("green", 2), ("b", -1)], seg);
    }

    [AvaloniaFact]
    public async Task Shell_tab_connects_sends_input_and_reports_exit()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        var pod = Pod(h, "crashy-0");
        await ws.OpenShellAsync(pod, "main");
        var term = Assert.IsType<TerminalViewModel>(ws.Dock.Selected);
        await UiHarness.WaitFor(() => term.Buffer.ScreenText().Contains("/ #"), what: "prompt");
        Assert.Equal("Connected", term.Status);
        await term.SendAsync("echo hi\r");
        await UiHarness.WaitFor(() => term.Buffer.ScreenText().Contains("echo hi"), what: "echo");
        await term.ResizeAsync(120, 40);
        await UiHarness.WaitFor(() => h.Demo.Server.ExecResizes.Contains((120, 40)), what: "resize frame");
        await term.SendAsync("exit\n");
        await UiHarness.WaitFor(() => term.Exited, what: "exit");
        Assert.Equal("Session ended", term.ExitMessage);
        h.Screenshot("14-terminal");
        await term.ReconnectCommand.ExecuteAsync(null);
        await UiHarness.WaitFor(() => !term.Exited && term.Buffer.ScreenText().Contains("reconnecting"), what: "reconnect");
    }

    [AvaloniaFact]
    public async Task Shell_on_read_only_cluster_is_refused_with_a_clear_message()
    {
        await using var h = await UiHarness.StartAsync();
        var m = h.Main.Settings.GetMetadata(h.Cluster("demo-staging").Id); m.SafetyMode = Core.Catalog.SafetyMode.ReadOnly;
        h.Main.Settings.SetMetadata(h.Cluster("demo-staging").Id, m);
        var ws = await h.ConnectAsync();
        await ws.OpenShellAsync(Pod(h, "crashy-0"), "main");
        var term = Assert.IsType<TerminalViewModel>(ws.Dock.Selected);
        Assert.True(term.Exited);
        Assert.Contains("read-only", term.ExitMessage, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Port_forward_tab_lists_forward_and_serves_requests()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        var pod = h.Demo.Server.List(Gvr.Pods, "default").First(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("web-"))["metadata"]!["name"]!.GetValue<string>();
        await ws.StartForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", pod, 8080));
        var pf = ws.Dock.Find<PortForwardsViewModel>();
        Assert.NotNull(pf);
        await UiHarness.WaitFor(() => pf!.Items.Count == 1);
        var row = pf!.Items[0];
        using var http = new HttpClient();
        Assert.Equal($"hello from {pod}:8080", await http.GetStringAsync(row.Forward.Url));
        row.CopyUrlCommand.Execute(null);
        await UiHarness.WaitFor(() => h.Clipboard.Text == row.Forward.Url);
        row.OpenCommand.Execute(null);
        Assert.Equal(row.Forward.Url, h.Browser.Opened.Single());
        h.Screenshot("15-port-forwards");
        await row.StopCommand.ExecuteAsync(null);
        await UiHarness.WaitFor(() => pf.Items.Count == 0);
    }

    [AvaloniaFact]
    public async Task Port_forward_to_service_from_the_service_actions()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        var svc = new KubeObject(h.Demo.Server.Get(Gvr.Services, "default", "web")!);
        var kind = ws.Session.Discovery.FindByName("services")!;
        var forward = ws.ActionsFor(kind, svc).First(a => a.Header.StartsWith("Forward"));
        await forward.ExecuteAsync();
        Assert.Single(ws.Session.PortForwards.Forwards);
        Assert.Equal(ForwardTargetKind.Service, ws.Session.PortForwards.Forwards[0].Target.Kind);
    }

    [AvaloniaFact]
    public async Task Closing_the_workspace_stops_everything_it_started()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        await ws.OpenPodLogsAsync(Pod(h, "crashy-0"), "main");
        await ws.StartForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", "crashy-0", 8080));
        var session = ws.Session;
        var token = session.SessionToken;
        await h.Main.CloseWorkspaceAsync(ws);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(Core.Sessions.SessionState.Disconnected, session.State);
        Assert.Empty(h.Main.Workspaces);
        Assert.True(h.Main.ShowWelcome);
        await UiHarness.WaitFor(() => h.Demo.Server.WatchCount == 0, what: "watches closed");
    }

    [AvaloniaFact]
    public async Task Two_clusters_are_connected_side_by_side_with_their_own_state()
    {
        await using var h = await UiHarness.StartAsync();
        var staging = await h.ConnectAsync("demo-staging");
        staging.Namespaces.Select("default");
        staging.OpenKind(staging.Session.Discovery.FindByName("pods")!);
        var prod = await h.ConnectAsync("demo-production");
        prod.Namespaces.Select("production");
        prod.OpenKind(prod.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => prod.CurrentContent is ResourceListViewModel p && p.Rows.Count == 3);
        Assert.Equal(2, h.Main.Workspaces.Count);
        h.Main.SelectWorkspaceByIndex(0);
        Assert.Same(staging, h.Main.SelectedWorkspace);
        var stagingList = Assert.IsType<ResourceListViewModel>(staging.CurrentContent);
        Assert.NotEqual(3, stagingList.Rows.Count); // its own namespace selection and list
        Assert.True(prod.IsProduction); Assert.False(staging.IsProduction);
        h.Main.SelectWorkspaceByIndex(1);
        h.Screenshot("16-two-clusters");
    }
}

public class UiKeepForwardTests
{
    [AvaloniaFact]
    public async Task Kept_forward_is_restored_when_the_cluster_is_connected_again()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        var pod = h.Demo.Server.List(Gvr.Pods, "default").First(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("web-"))["metadata"]!["name"]!.GetValue<string>();
        await ws.StartForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, "default", pod, 8080));
        var row = ws.Dock.Find<PortForwardsViewModel>()!.Items[0];
        row.Keep = true;
        Assert.Single(ws.Session.Metadata.KeptForwards);
        await h.Main.CloseWorkspaceAsync(ws);
        var ws2 = await h.ConnectAsync();
        await UiHarness.WaitFor(() => ws2.Session.PortForwards.Forwards.Count == 1, what: "restored forward");
        Assert.True(ws2.Session.PortForwards.Forwards[0].Keep);
        using var http = new HttpClient();
        Assert.StartsWith("hello from", await http.GetStringAsync(ws2.Session.PortForwards.Forwards[0].Url));
        // Stopping a forward from the list forgets it.
        var pfvm = ws2.Dock.Find<PortForwardsViewModel>()!;
        await UiHarness.WaitFor(() => pfvm.Items.Count == 1);
        await pfvm.Items[0].StopCommand.ExecuteAsync(null);
        Assert.Empty(ws2.Session.Metadata.KeptForwards);
    }
}
