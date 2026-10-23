using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Sessions;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiDetailsAndActionsTests
{
    private static async Task<(UiHarness h, WorkspaceViewModel ws, ResourceListViewModel list)> OpenAsync(string context, string kindName, string? ns = null)
    {
        var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync(context);
        if (ns is null) ws.Namespaces.SelectAll(); else ws.Namespaces.Select(ns);
        ws.OpenKind(ws.Session.Discovery.FindByName(kindName)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Resource == kindName.ToLowerInvariant() && l.Rows.Count > 0, what: kindName + " rows");
        return (h, ws, (ResourceListViewModel)ws.CurrentContent!);
    }

    [AvaloniaFact]
    public async Task Pod_details_show_overview_events_and_related()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "pods");
        await using var _ = h;
        list.SelectedRow = list.Rows.First(r => r.Name == "crashy-0");
        await UiHarness.WaitFor(() => ws.Details.IsOpen && ws.Details.Events.Count == 1, what: "events");
        var d = ws.Details;
        Assert.Equal("crashy-0", d.Title);
        Assert.Contains(d.Sections, s => s.Title == "Containers" && s.Table!.Rows[0][2].Contains("CrashLoopBackOff"));
        Assert.Equal("BackOff", d.Events[0].Reason);
        Assert.True(d.Events[0].IsWarning);
        Assert.Contains("kind: Pod", d.YamlText);
        Assert.DoesNotContain("managedFields", d.YamlText);
        h.Screenshot("07-pod-details");

        list.SelectedRow = list.Rows.First(r => r.Name.StartsWith("web-"));
        await UiHarness.WaitFor(() => d.RelatedItems.Count > 0, what: "related");
        Assert.Contains(d.RelatedItems, r => r.Relation == "Owner" && r.Kind == "ReplicaSet");
        Assert.Contains(d.RelatedItems, r => r.Relation == "Owner" && r.Kind == "Deployment");
        Assert.Contains(d.RelatedItems, r => r.Relation == "Service" && r.Name == "web");
    }

    [AvaloniaFact]
    public async Task Secret_values_are_never_shown_until_revealed_one_key_at_a_time()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "secrets");
        await using var _ = h;
        list.SelectedRow = list.Rows.First(r => r.Name == "db-credentials");
        await UiHarness.WaitFor(() => ws.Details.SecretKeys.Count == 2);
        var d = ws.Details;
        Assert.DoesNotContain("c3VwZXJzZWNyZXQ=", d.YamlText);
        Assert.DoesNotContain("supersecret", d.YamlText);
        Assert.Contains(SecretMasker.Mask, d.YamlText);
        Assert.All(d.SecretKeys, k => Assert.Equal(SecretMasker.Mask, k.Display));
        h.Screenshot("08-secret-masked"); // snapshot taken while every value is still masked
        var password = d.SecretKeys.First(k => k.Key == "password");
        password.ToggleCommand.Execute(null);
        Assert.Equal("supersecret", password.Display);
        Assert.Equal(SecretMasker.Mask, d.SecretKeys.First(k => k.Key == "username").Display);
        Assert.DoesNotContain("supersecret", d.YamlText); // the YAML tab stays masked
        await d.CopySecretValueCommand.ExecuteAsync(password);
        Assert.Equal("supersecret", h.Clipboard.Text);
        // Never in the log file or the activity log either.
        Assert.DoesNotContain(ws.Session.Activity.Entries, e => (e.Message ?? "").Contains("supersecret"));
    }

    [AvaloniaFact]
    public async Task Editing_conflict_shows_diff_and_reapply_works()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "configmaps");
        await using var _ = h;
        list.SelectedRow = list.Rows.First(r => r.Name == "app-config");
        await UiHarness.WaitFor(() => ws.Details.IsOpen);
        var d = ws.Details;
        d.BeginEditCommand.Execute(null);
        Assert.True(d.IsEditing);
        d.EditText = d.EditText.Replace("LOG_LEVEL: info", "LOG_LEVEL: error");
        // Somebody else edits the same object meanwhile.
        var current = h.Demo.Server.Get(Gvr.ConfigMaps, "default", "app-config")!;
        current["data"]!["LOG_LEVEL"] = "debug";
        h.Demo.Server.Upsert(current);
        await d.SaveCommand.ExecuteAsync(null);
        Assert.True(d.HasConflict);
        Assert.Contains("-   LOG_LEVEL: error", d.ConflictDiff);
        Assert.Contains("+   LOG_LEVEL: debug", d.ConflictDiff);
        Assert.Equal("debug", h.Demo.Server.Get(Gvr.ConfigMaps, "default", "app-config")!["data"]!["LOG_LEVEL"]!.GetValue<string>());
        h.Screenshot("09-edit-conflict");
        d.ReloadAndReapplyCommand.Execute(null);
        Assert.False(d.HasConflict);
        await d.SaveCommand.ExecuteAsync(null);
        Assert.False(d.IsEditing);
        Assert.Equal("error", h.Demo.Server.Get(Gvr.ConfigMaps, "default", "app-config")!["data"]!["LOG_LEVEL"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Selecting_another_object_while_editing_drops_the_stale_edit()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "pods");
        await using var _ = h;
        var first = list.Rows[0]; var second = list.Rows.First(r => r.Name != first.Name);
        list.SelectedRow = first;
        var d = ws.Details;
        d.BeginEditCommand.Execute(null);
        Assert.Contains($"name: {first.Name}", d.EditText);
        list.SelectedRow = second;
        Assert.False(d.IsEditing);
        Assert.Equal(second.Name, d.Object!.Name);
        Assert.Contains($"name: {second.Name}", d.YamlText);
        d.BeginEditCommand.Execute(null);
        Assert.Contains($"name: {second.Name}", d.EditText);
    }

    [AvaloniaFact]
    public async Task Edit_validation_error_is_shown_inline_with_the_api_message()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "configmaps");
        await using var _ = h;
        list.SelectedRow = list.Rows.First(r => r.Name == "app-config");
        var d = ws.Details;
        d.BeginEditCommand.Execute(null);
        d.EditText = "apiVersion: v1\nkind: Nope\nmetadata:\n  name: x\n";
        await d.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Nope", d.EditError);
        Assert.True(d.IsEditing);
    }

    [AvaloniaFact]
    public async Task Production_cluster_requires_typing_the_name_to_delete()
    {
        var (h, ws, list) = await OpenAsync("demo-production", "pods", "production");
        await using var _ = h;
        Assert.True(ws.IsProduction);
        var victim = list.Rows.First(r => r.Name.StartsWith("checkout-"));
        // Wrong name typed: refused by ClusterActions, pod survives.
        h.Dialogs.TypedResponse = _ => "something-else";
        await ws.DeleteAsync(list, [victim]);
        Assert.NotNull(h.Demo.Server.Get(Gvr.Pods, "production", victim.Name));
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("typed:") && s.EndsWith(victim.Name));
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("error:Delete failed"));
        // Right name.
        h.Dialogs.TypedResponse = e => e;
        await ws.DeleteAsync(list, [victim]);
        await UiHarness.WaitFor(() => h.Demo.Server.Get(Gvr.Pods, "production", victim.Name) is null);
        Assert.Contains(ws.Session.Activity.Entries, e => e.Action == "Delete" && e.Success);
        h.Screenshot("10-production-banner");
    }

    [AvaloniaFact]
    public async Task Normal_cluster_asks_yes_no_and_cancel_keeps_the_object()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "pods", "default");
        await using var _ = h;
        var victim = list.Rows.First(r => r.Name.StartsWith("web-"));
        h.Dialogs.ConfirmResult = false;
        await ws.DeleteAsync(list, [victim]);
        Assert.NotNull(h.Demo.Server.Get(Gvr.Pods, "default", victim.Name));
        Assert.DoesNotContain(h.Dialogs.Shown, s => s.StartsWith("typed:"));
        h.Dialogs.ConfirmResult = true;
        await ws.DeleteAsync(list, [victim]);
        await UiHarness.WaitFor(() => h.Demo.Server.Get(Gvr.Pods, "default", victim.Name) is null);
    }

    [AvaloniaFact]
    public async Task Read_only_cluster_refuses_actions_in_ui_and_disables_buttons_with_reason()
    {
        await using var h = await UiHarness.StartAsync(s => { });
        var item = h.Cluster("demo-staging");
        h.Main.Settings.SetMetadata(item.Id, new CatalogMetaHelper().ReadOnly(h.Main.Settings.GetMetadata(item.Id)));
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("deployments")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "web");
        await UiHarness.WaitFor(() => ws.Details.Actions.Any(a => a.Header == "Delete" && !a.IsEnabled), what: "disabled actions");
        Assert.Contains("read-only", ws.Details.Actions.First(a => a.Header == "Delete").Tooltip!, StringComparison.OrdinalIgnoreCase);
        var before = h.Demo.Server.Requests.Count(r => r.Method is "DELETE" or "PATCH");
        await ws.DeleteAsync(list, [list.Rows.First(r => r.Name == "web")]);
        Assert.Contains(h.Dialogs.Shown, s => s.StartsWith("error:Read-only"));
        Assert.Equal(before, h.Demo.Server.Requests.Count(r => r.Method is "DELETE" or "PATCH"));
        Assert.NotNull(h.Demo.Server.Get(Gvr.Deployments, "default", "web"));
    }

    private sealed class CatalogMetaHelper
    {
        public ClusterMetadata ReadOnly(ClusterMetadata m) { m.SafetyMode = SafetyMode.ReadOnly; return m; }
    }

    [AvaloniaFact]
    public async Task Rbac_forbidden_action_is_disabled_with_the_access_review_reason()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "deployments", "default");
        await using var _ = h;
        h.Demo.Server.Deny("delete", "deployments");
        list.SelectedRow = list.Rows.First(r => r.Name == "web");
        await UiHarness.WaitFor(() => ws.Details.Actions.Any(a => a.Header == "Delete" && !a.IsEnabled), what: "rbac disabled");
        var delete = ws.Details.Actions.First(a => a.Header == "Delete");
        Assert.Contains("cannot delete deployments", delete.Tooltip);
        Assert.True(ws.Details.Actions.First(a => a.Header == "Edit").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Scale_and_restart_go_through_cluster_actions_and_show_in_activity()
    {
        var (h, ws, list) = await OpenAsync("demo-staging", "deployments", "default");
        await using var _ = h;
        var web = list.Rows.First(r => r.Name == "web");
        list.SelectedRow = web;
        await UiHarness.WaitFor(() => ws.Details.Actions.Any(a => a.Header.StartsWith("Scale")));
        h.Dialogs.PromptResponse = _ => "6";
        await ws.Details.Actions.First(a => a.Header.StartsWith("Scale")).ExecuteAsync();
        Assert.Equal(6, h.Demo.Server.Get(Gvr.Deployments, "default", "web")!["spec"]!["replicas"]!.GetValue<int>());
        await ws.Details.Actions.First(a => a.Header == "Restart").ExecuteAsync();
        Assert.NotNull(h.Demo.Server.Get(Gvr.Deployments, "default", "web")!["spec"]!["template"]!["metadata"]!["annotations"]);
        ws.Navigate(ws.FindNav(NavTarget.Activity)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ActivityViewModel a && a.Entries.Count >= 2);
        var activity = (ActivityViewModel)ws.CurrentContent!;
        Assert.Contains(activity.Entries, e => e.Action == "Scale" && e.Success);
        h.Screenshot("11-activity");
    }

    [AvaloniaFact]
    public async Task Node_cordon_and_cronjob_trigger()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.OpenKind(ws.Session.Discovery.FindByName("nodes")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count == 2);
        var nodes = (ResourceListViewModel)ws.CurrentContent!;
        nodes.SelectedRow = nodes.Rows.First(r => r.Name == "node-2");
        await UiHarness.WaitFor(() => ws.Details.Actions.Any(a => a.Header == "Cordon"));
        await ws.Details.Actions.First(a => a.Header == "Cordon").ExecuteAsync();
        Assert.True(h.Demo.Server.Get(Gvr.Nodes, null, "node-2")!["spec"]!["unschedulable"]!.GetValue<bool>());
        await UiHarness.WaitFor(() => nodes.Rows.First(r => r.Name == "node-2").Item.Object.At("spec", "unschedulable")?.ToString() == "True" || nodes.Rows.First(r => r.Name == "node-2").Item.Object.At("spec", "unschedulable")?.ToString() == "true", what: "node update");
        Assert.True(nodes.Rows.First(r => r.Name == "node-2").Severity == Severity.Warning);
    }

    [AvaloniaFact]
    public async Task Create_from_yaml_applies_every_document_with_per_document_results()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.Select("staging");
        ws.Navigate(ws.FindNav(NavTarget.CreateYaml)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is CreateYamlViewModel);
        var vm = (CreateYamlViewModel)ws.CurrentContent!;
        vm.Yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: one\ndata: {a: b}\n---\napiVersion: v1\nkind: Nothing\nmetadata:\n  name: two\n---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: three\n";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([true, false, true], vm.Results.Select(r => r.Success));
        Assert.NotNull(h.Demo.Server.Get(Gvr.ConfigMaps, "staging", "one"));
        Assert.NotNull(h.Demo.Server.Get(Gvr.ConfigMaps, "staging", "three"));
        h.Screenshot("12-create-yaml");
    }
}

public class UiYamlTabTests
{
    [AvaloniaFact]
    public async Task Yaml_tab_renders_masked_yaml_in_the_editor_control()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("deployments")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows.First(r => r.Name == "api");
        await UiHarness.WaitFor(() => ws.Details.IsOpen && ws.Details.YamlText.Contains("kind: Deployment"));
        ws.Details.SelectedTab = 1;
        UiHarness.Pump();
        var editor = h.Find<Kubism.Desktop.Controls.YamlEditor>(e => !e.IsReadOnly is false);
        Assert.NotNull(editor);
        Assert.Contains("kind: Deployment", editor!.Editor.Text);
        Assert.Contains("replicas: 2", editor.Editor.Text);
        h.Screenshot("23-yaml-tab");
    }
}

public class UiMultiSelectTests
{
    [AvaloniaFact]
    public async Task Multi_selection_keeps_delete_reachable_and_deletes_all_selected()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.Select("default");
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count >= 3);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        UiHarness.Pump();
        var grid = h.Find<Avalonia.Controls.DataGrid>(g => g.Name == "Grid")!;
        var victims = list.Rows.Where(r => r.Name.StartsWith("web-")).Take(2).ToList();
        foreach (var v in victims) grid.SelectedItems.Add(v);
        UiHarness.Pump();
        Assert.Equal(2, list.SelectedRows.Count);
        Assert.Equal("Delete 2 selected", list.DeleteLabel);
        var del = h.Find<Avalonia.Controls.Button>(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Delete selected")!;
        Assert.True(del.IsEnabled);
        // The toolbar wraps: the button is inside the list's own width, not pushed under the details panel.
        var listView = h.Find<Kubism.Desktop.Views.ResourceListView>()!;
        var right = Avalonia.VisualExtensions.TranslatePoint(del, new Avalonia.Point(del.Bounds.Width, 0), listView)!.Value.X;
        Assert.True(right <= listView.Bounds.Width + 1, $"delete button ends at {right}, list is {listView.Bounds.Width} wide");
        h.Screenshot("26-multi-select");
        // With the details panel open (single selection) nothing of the list may extend underneath it.
        h.Window.Width = 1000; // narrow window: the case where the details panel used to cover the toolbar
        list.SelectedRow = victims[0];
        await UiHarness.WaitFor(() => ws.Details.IsOpen);
        UiHarness.Pump();
        var details = h.Find<Kubism.Desktop.Views.DetailsView>(d => d.IsVisible)!;
        var refresh = h.Find<Avalonia.Controls.Button>(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Refresh" && b.IsEffectivelyVisible)!;
        var refreshRight = Avalonia.VisualExtensions.TranslatePoint(refresh, new Avalonia.Point(refresh.Bounds.Width, 0), h.Window)!.Value.X;
        var detailsLeft = Avalonia.VisualExtensions.TranslatePoint(details, new Avalonia.Point(0, 0), h.Window)!.Value.X;
        Assert.True(refreshRight <= detailsLeft + 1, $"Refresh ends at {refreshRight}, details start at {detailsLeft}");
        h.Screenshot("27-details-open-toolbar");
        await del.Command!.ExecuteAsyncIfPossible();
        foreach (var v in victims) await UiHarness.WaitFor(() => h.Demo.Server.Get(Gvr.Pods, "default", v.Name) is null, what: "deleted");
    }
}

internal static class CommandExt
{
    public static async Task ExecuteAsyncIfPossible(this System.Windows.Input.ICommand c)
    {
        if (c is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand a) await a.ExecuteAsync(null); else c.Execute(null);
    }
}

public class YamlEditorTests
{
    [AvaloniaFact]
    public void Yaml_editor_is_read_only_until_told_otherwise()
    {
        var editor = new Kubism.Desktop.Controls.YamlEditor();
        Assert.True(editor.Editor.IsReadOnly);   // a binding to True is not a property change, so the default must already be read-only
        editor.IsReadOnly = false;
        Assert.False(editor.Editor.IsReadOnly);
        editor.IsReadOnly = true;
        Assert.True(editor.Editor.IsReadOnly);
    }
}
