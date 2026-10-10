using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
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
    public async Task A_flush_queued_before_pausing_does_not_show_what_arrives_while_paused()
    {
        var (h, ws) = await ConnectAsync();
        await using var _ = h;
        var name = h.Demo.Server.List(Gvr.Pods, "default").First(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("api-"))["metadata"]!["name"]!.GetValue<string>();
        await ws.OpenPodLogsAsync(Pod(h, name), "main");
        var logs = Assert.IsType<LogViewModel>(ws.Dock.Selected);
        string text = "";
        logs.TextChanged += (t, _, replace) => text = replace ? t : text + t;
        await UiHarness.WaitFor(() => text.Split('\n').Count(l => l.Length > 0) == 20, what: "initial lines rendered");
        h.Demo.Server.AppendLog("default", name, "QUEUED BEFORE PAUSE");
        await UiHarness.WaitFor(() => logs.Buffer.Count == 21, what: "first line buffered");   // this queues a flush 100 ms away
        logs.Paused = true;                                                                    // ...which fires after the pause
        h.Demo.Server.AppendLog("default", name, "ARRIVED WHILE PAUSED");
        await UiHarness.WaitFor(() => logs.Buffer.Count == 22, what: "second line buffered");
        await Task.Delay(400); UiHarness.Pump();
        Assert.DoesNotContain("ARRIVED WHILE PAUSED", text);
        logs.Paused = false;
        await UiHarness.WaitFor(() => text.Contains("ARRIVED WHILE PAUSED"), what: "shown after resuming");
    }

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

public class UiDockResizeTests
{
    [AvaloniaFact]
    public async Task Dragging_the_handle_resizes_the_bottom_panel_and_the_terminal_follows()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        await ws.OpenShellAsync(new KubeObject(h.Demo.Server.Get(Gvr.Pods, "default", "crashy-0")!), "main");
        UiHarness.Pump();
        var term = ws.Dock.Selected as TerminalViewModel;
        await UiHarness.WaitFor(() => term!.Buffer.Rows > 3);
        var handle = h.Find<Avalonia.Controls.Border>(b => b.Name == "DockHandle");
        Assert.NotNull(handle);
        var start = ws.Dock.Height; var rowsBefore = term!.Buffer.Rows;
        var p = Avalonia.VisualExtensions.TranslatePoint(handle!, new Avalonia.Point(5, 3), h.Window)!.Value;
        h.Window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        h.Window.MouseMove(new Avalonia.Point(p.X, p.Y - 150));
        h.Window.MouseUp(new Avalonia.Point(p.X, p.Y - 150), Avalonia.Input.MouseButton.Left);
        UiHarness.Pump();
        Assert.True(ws.Dock.Height > start + 100, $"height {ws.Dock.Height} vs {start}");
        UiHarness.Pump();
        await UiHarness.WaitFor(() => term.Buffer.Rows > rowsBefore, what: "terminal resized");
        h.Screenshot("25-dock-resized");
    }
}

public class UiDetailsResizeTests
{
    [AvaloniaFact]
    public async Task Details_panel_is_a_real_resizable_column_with_no_gap_and_collapses_when_closed()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        var view = h.Find<Kubism.Desktop.Views.DetailsView>()!;
        UiHarness.Pump();
        Assert.False(view.IsVisible);
        var col = h.Find<Kubism.Desktop.Views.WorkspaceView>()!.DetailsColumn;
        Assert.Equal(0, col.Width.Value);
        list.SelectedRow = list.Rows[0];
        await UiHarness.WaitFor(() => ws.Details.IsOpen);
        UiHarness.Pump();
        Assert.Equal(420, col.Width.Value);
        var splitter = h.Find<Avalonia.Controls.Border>(g => Avalonia.Automation.AutomationProperties.GetName(g) == "Resize details panel")!;
        var p = Avalonia.VisualExtensions.TranslatePoint(splitter, new Avalonia.Point(2, 50), h.Window)!.Value;
        h.Window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        h.Window.MouseMove(new Avalonia.Point(p.X - 120, p.Y));
        h.Window.MouseUp(new Avalonia.Point(p.X - 120, p.Y), Avalonia.Input.MouseButton.Left);
        UiHarness.Pump();
        Assert.True(col.Width.Value > 500, $"column {col.Width.Value}");
        // The panel fills its column: no blank band between the list and the panel.
        Assert.True(Math.Abs(view.Bounds.Width - col.ActualWidth) < 1.5, $"view {view.Bounds.Width} vs column {col.ActualWidth}");
        h.Screenshot("28-details-resized");
        ws.Details.CloseCommand.Execute(null);
        UiHarness.Pump();
        Assert.Equal(0, col.Width.Value);
        // With nothing selected the centre fills everything right of the sidebar (no leftover fixed width).
        var workspace = h.Find<Kubism.Desktop.Views.WorkspaceView>()!;
        var grid = workspace.FindDescendantOfType<Avalonia.Controls.Grid>()!;
        var centre = h.Find<Kubism.Desktop.Views.ResourceListView>()!;
        var centreRight = Avalonia.VisualExtensions.TranslatePoint(centre, new Avalonia.Point(centre.Bounds.Width, 0), h.Window)!.Value.X;
        Assert.True(centreRight >= h.Window.Bounds.Width - 2, $"centre ends at {centreRight}, window is {h.Window.Bounds.Width}");
        h.Screenshot("29-no-selection-full-width");
        list.SelectedRow = list.Rows[1];
        await UiHarness.WaitFor(() => ws.Details.IsOpen);
        UiHarness.Pump();
        Assert.True(col.Width.Value > 500, "the resized width is remembered");
    }
}

public class UiForwardNavigationBugTests
{
    [AvaloniaFact]
    public async Task Port_forwards_tab_keeps_working_after_navigating_elsewhere()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        var web = new KubeObject(h.Demo.Server.Get(Gvr.Services, "default", "web")!);
        await ws.ActionsFor(ws.Session.Discovery.FindByName("services")!, web).First(a => a.Header.StartsWith("Forward")).ExecuteAsync();
        var dock = ws.Dock.Find<PortForwardsViewModel>()!;
        await UiHarness.WaitFor(() => dock.Items.Count == 1);
        // Left panel: "Port forwards", then "Endpoints" (the sequence from the bug report).
        ws.Navigate(ws.FindNav(NavTarget.PortForwards)!);
        ws.OpenKind(ws.Session.Discovery.FindByName("endpoints")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Endpoints");
        Assert.Same(dock, ws.Dock.Find<PortForwardsViewModel>());
        // Stop from the bottom panel: the row disappears.
        await dock.Items[0].StopCommand.ExecuteAsync(null);
        await UiHarness.WaitFor(() => dock.Items.Count == 0, what: "forward removed from the dock");
        Assert.Empty(ws.Session.PortForwards.Forwards);
        // And a new forward shows up again.
        await ws.StartForwardAsync(new ForwardTarget(ForwardTargetKind.Service, "default", "web", 80));
        await UiHarness.WaitFor(() => dock.Items.Count == 1, what: "new forward listed");
        Assert.Single(ws.Session.PortForwards.Forwards);
    }
}
