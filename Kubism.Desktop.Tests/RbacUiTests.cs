using Avalonia.Headless.XUnit;
using Kubism.Core.Security;
using Kubism.Desktop.Controls;
using Kubism.Desktop.ViewModels;
using Xunit;

namespace Kubism.Desktop.Tests;

public class RbacUiTests
{
    private static async Task<(UiHarness h, WorkspaceViewModel ws, RbacViewModel vm)> OpenAsync()
    {
        var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        var node = ws.FindNav(NavTarget.Rbac);
        Assert.NotNull(node);
        ws.Navigate(node!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RbacViewModel v && !v.IsLoading && v.Chain is { Nodes.Count: > 0 }, what: "rbac graph");
        return (h, ws, (RbacViewModel)ws.CurrentContent!);
    }

    [AvaloniaFact]
    public async Task Entry_sits_under_Access_Control_and_the_chain_draws_four_columns_with_scope_and_the_cluster_role_in_a_namespace()
    {
        var (h, ws, vm) = await OpenAsync();
        await using var _ = h;
        var access = ws.NavRoots.Single(n => n.Title == "Access Control");
        Assert.Contains(access.Children, n => n.Target == NavTarget.Rbac);

        Assert.True(vm.Graph.IsComplete);
        Assert.False(vm.HasWarning);
        var chain = vm.Chain!;
        Assert.Equal(new[] { 0, 1, 2, 3 }, chain.Nodes.Select(n => n.Column).Distinct().Order());
        Assert.Contains(chain.Edges, e => e.Highlight && e.Label.Contains("staging only"));            // devs-edit: RoleBinding → ClusterRole
        Assert.Contains(chain.Nodes, n => n.Missing && n.Label == "removed-role");                      // binding to a role that does not exist
        Assert.Contains(chain.Nodes, n => n.Kind == RbacNodeKind.Binding && n.Scope == "production");   // namespace scope
        Assert.Contains(chain.Nodes, n => n.Kind == RbacNodeKind.Binding && n.Scope is null);           // cluster scope
        Assert.NotNull(h.Find<RbacGraphControl>());
        h.Screenshot("60-rbac-chain");

        // filters
        vm.SelectedNamespace = "production";
        Assert.All(vm.Chain!.Nodes.Where(n => n.Kind == RbacNodeKind.Binding && n.Scope is not null), n => Assert.Equal("production", n.Scope));
        vm.SelectedNamespace = "All namespaces";
        vm.RiskyOnly = true;
        Assert.Contains(vm.Chain!.Nodes, n => n.Label == "legacy-open-secrets");
        Assert.DoesNotContain(vm.Chain!.Nodes, n => n.Label == "node-readers");
        vm.RiskyOnly = false;
        vm.SubjectSearch = "alice";
        Assert.Single(vm.Chain!.Nodes, n => n.Kind == RbacNodeKind.Subject);
        vm.SubjectSearch = "";
    }

    [AvaloniaFact]
    public async Task Clicking_a_subject_switches_to_the_subject_view_with_a_matrix_and_paths()
    {
        var (h, _, vm) = await OpenAsync();
        await using var _ = h;
        var subject = vm.Chain!.Nodes.First(n => n.Kind == RbacNodeKind.Subject && n.Subject is { Kind: "Group", Name: "devs" });
        vm.NodeClickedCommand.Execute(subject);
        Assert.Equal(RbacViewModel.SubjectTab, vm.Tab);
        Assert.Equal("Group devs", vm.SubjectText);
        Assert.Contains("staging", vm.SubjectNamespaces);
        vm.SelectedSubjectNamespace = "staging";
        UiHarness.Pump();
        var exec = vm.MatrixRows.Single(r => r.Resource == "pods/exec");
        var create = exec.Cells[vm.MatrixVerbs.ToList().IndexOf("create")];
        Assert.Equal("✔", create.Text);
        Assert.True(create.Risky);
        Assert.Contains(vm.SubjectPaths, p => p.Binding.Contains("devs-edit"));
        Assert.Contains(vm.SubjectPaths, p => p.Note.Contains("staging only"));
        h.Screenshot("61-rbac-subject");

        vm.SelectedSubjectNamespace = "(cluster-wide)";
        UiHarness.Pump();
        Assert.Contains(vm.MatrixRows, r => r.Resource == "nodes");
        Assert.DoesNotContain(vm.MatrixRows, r => r.Resource == "pods/exec");
    }

    [AvaloniaFact]
    public async Task Who_can_lists_subjects_with_paths_and_compares_with_the_api_server()
    {
        var (h, _, vm) = await OpenAsync();
        await using var _ = h;
        vm.Tab = RbacViewModel.ResourceTab;
        vm.Verb = "get"; vm.ResourceText = "secrets"; vm.ResourceNamespace = "staging";
        vm.SearchCommand.Execute(null);
        Assert.Contains(vm.WhoCanRows, r => r.Entry.Subject.Key == "Group:devs");
        var everyone = vm.WhoCanRows.Single(r => r.Entry.Subject.Key == "Group:system:authenticated");
        Assert.Equal("everyone", everyone.Breadth);
        Assert.Equal(Severity.Error, everyone.Severity);
        vm.SelectedWho = vm.WhoCanRows.Single(r => r.Entry.Subject.Key == "Group:devs");
        Assert.Contains(vm.WhoPaths, p => p.Binding.Contains("devs-edit") && p.Role.Contains("edit") && p.Rule.Contains("secrets"));
        Assert.Contains("Who can get secrets", vm.WhoSummary);
        h.Screenshot("62-rbac-who-can");

        // the fake API server is not RBAC-backed: it answers "allowed", which differs from what the objects say for "me" (fake user holds nothing)
        h.Demo.Server.FakeUser = "nobody"; h.Demo.Server.FakeGroups = ["system:authenticated"];
        vm.Verb = "delete"; vm.ResourceText = "nodes"; vm.ResourceNamespace = "(cluster-wide)";
        await vm.VerifyMeCommand.ExecuteAsync(null);
        await UiHarness.WaitFor(() => vm.VerifyText.Length > 0, what: "verification");
        Assert.Contains("RBAC objects say denied", vm.VerifyText);
        Assert.Contains("API server says allowed", vm.VerifyText);
        Assert.Contains("another authorizer", vm.VerifyText);
    }

    [AvaloniaFact]
    public async Task An_unreadable_kind_shows_a_warning_instead_of_a_silent_partial_graph()
    {
        await using var h = await UiHarness.StartAsync();
        h.Demo.Server.Deny("list", "rolebindings");
        var ws = await h.ConnectAsync("demo-staging");
        ws.Namespaces.SelectAll();
        ws.Navigate(ws.FindNav(NavTarget.Rbac)!);
        await UiHarness.WaitFor(() => ws.CurrentContent is RbacViewModel v && !v.IsLoading, what: "rbac");
        var vm = (RbacViewModel)ws.CurrentContent!;
        await UiHarness.WaitFor(() => vm.HasWarning, what: "warning");
        Assert.Contains("rolebindings", vm.Warning);
        Assert.False(vm.Graph.IsComplete);
        Assert.Contains("Incomplete", vm.Warning);
        h.Screenshot("63-rbac-incomplete");
    }
}
