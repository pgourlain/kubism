using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Drain;
using Kubism.Desktop.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Kubism.Desktop.Tests;

public class DrainUiTests
{
    private static JsonObject J(string json) => JsonNode.Parse(json.Replace('\'', '"'))!.AsObject();

    private static int Writes(UiHarness h) => h.Demo.Server.Requests.Count(r => r.Method is "PATCH" or "DELETE" || r.Path.EndsWith("/eviction"));

    /// <summary>Two nodes of an "old" pool (the demo ones) and one of a "new" pool, then opens the Drain page.</summary>
    private static async Task<DrainViewModel> OpenDrainAsync(UiHarness h, WorkspaceViewModel ws)
    {
        foreach (var name in new[] { "node-1", "node-2" })
        {
            var n = h.Demo.Server.Get(Gvr.Nodes, null, name)!;
            n["metadata"]!["labels"]!["eks.amazonaws.com/nodegroup"] = "old";
            h.Demo.Server.Upsert(n);
        }
        h.Demo.Server.Upsert(J("{'apiVersion':'v1','kind':'Node','metadata':{'name':'new-1','uid':'n-new-1','labels':{'eks.amazonaws.com/nodegroup':'new'}},'spec':{},'status':{'allocatable':{'cpu':'16','memory':'64Gi','pods':'110'},'conditions':[{'type':'Ready','status':'True'}]}}"));
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Drain)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is DrainViewModel v && v.Nodes.Count >= 3 && !v.IsLoading, what: "drain page");
        return (DrainViewModel)ws.CurrentContent!;
    }

    [AvaloniaFact]
    public async Task Choosing_and_reading_change_nothing_and_the_drain_needs_a_confirmation()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var vm = await OpenDrainAsync(h, ws);
        var before = Writes(h);

        // The pool label is detected (whatever the provider) and the pool is ticked in one click.
        Assert.Equal("eks.amazonaws.com/nodegroup", vm.GroupLabel);
        Assert.Equal(["new (1)", "old (2)"], vm.Groups.Select(g => g.Text));
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanDrain);
        vm.SelectGroupCommand.Execute("old");
        Assert.Equal(["node-1", "node-2"], vm.SelectedNames);
        h.Screenshot("70-drain-page");

        // crashy-0 is a bare pod: the plan says so, and the button stays off until the user explicitly accepts it.
        Assert.False(vm.Plan.Plan.CanComplete);
        Assert.Contains(vm.Plan.Plan.Pods, p => p.Name == "crashy-0" && p.Severity == DrainSeverity.Blocker && p.Reason.Contains("Bare pod"));
        Assert.False(vm.CanDrain);
        Assert.False(vm.DrainCommand.CanExecute(null));
        Assert.Equal(before, Writes(h));

        // Accepting the blockers enables the button, but pressing it only asks: declining changes nothing.
        vm.Plan.DrainAnyway = true;
        Assert.True(vm.DrainCommand.CanExecute(null));
        h.Dialogs.ConfirmResult = false;
        await vm.DrainCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("confirm:Drain nodes:2 nodes"));
        Assert.Equal(before, Writes(h));

        // Confirming cordons every chosen node and evicts.
        h.Dialogs.ConfirmResult = true;
        await vm.DrainCommand.ExecuteAsync(null);
        Assert.True(h.Demo.Server.Get(Gvr.Nodes, null, "node-1")!["spec"]!["unschedulable"]!.GetValue<bool>());
        Assert.True(h.Demo.Server.Get(Gvr.Nodes, null, "node-2")!["spec"]!["unschedulable"]!.GetValue<bool>());
        Assert.False(h.Demo.Server.Get(Gvr.Nodes, null, "new-1")!["spec"]?["unschedulable"]?.GetValue<bool>() ?? false);
        Assert.Null(h.Demo.Server.Get(Gvr.Pods, "default", "crashy-0"));
    }

    [AvaloniaFact]
    public async Task A_node_action_opens_the_page_with_the_node_ticked_and_does_not_drain()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        var nodeKind = ws.Session.Discovery.FindByName("nodes")!;
        var node = new KubeObject(h.Demo.Server.Get(Gvr.Nodes, null, "node-1")!);
        var before = Writes(h);
        var action = ws.ActionsFor(nodeKind, node).Single(a => a.Header.StartsWith("Drain"));
        await action.ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is DrainViewModel v && v.Nodes.Count > 0 && !v.IsLoading, what: "drain page");
        var vm = (DrainViewModel)ws.CurrentContent!;
        Assert.Equal(["node-1"], vm.SelectedNames);
        Assert.Equal(before, Writes(h));
        Assert.DoesNotContain(h.Dialogs.Shown, s => s.StartsWith("confirm:"));
    }

    [AvaloniaFact]
    public async Task Production_asks_for_the_node_count_and_read_only_refuses()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var vm = await OpenDrainAsync(h, ws);
        vm.SelectGroupCommand.Execute("old");
        vm.Plan.DrainAnyway = true;

        var meta = h.Main.Settings.GetMetadata(ws.Session.Entry.Id);
        meta.SafetyMode = SafetyMode.Production; h.Main.Settings.SetMetadata(ws.Session.Entry.Id, meta);
        h.Dialogs.TypedResponse = _ => null;   // the user does not type it
        await vm.DrainCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s == "typed:Drain on production:2");
        Assert.False(h.Demo.Server.Get(Gvr.Nodes, null, "node-1")!["spec"]?["unschedulable"]?.GetValue<bool>() ?? false);

        meta.SafetyMode = SafetyMode.ReadOnly; h.Main.Settings.SetMetadata(ws.Session.Entry.Id, meta);
        h.Dialogs.Shown.Clear();
        await vm.DrainCommand.ExecuteAsync(null);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("error:Read-only cluster"));
        Assert.Equal(0, Writes(h));
    }

    [AvaloniaFact]
    public async Task Options_and_the_label_can_change_without_touching_the_cluster()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        var vm = await OpenDrainAsync(h, ws);
        vm.Select(["node-1"]);
        vm.Plan.Show = "Blockers";
        Assert.All(vm.Plan.Rows, r => Assert.Equal(DrainSeverity.Blocker, r.Severity));
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.Plan.HasNodes);
        Assert.Empty(vm.Plan.Rows);
        Assert.Equal(0, Writes(h));
    }
}
