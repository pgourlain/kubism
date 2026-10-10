using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Security;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

/// <summary>
/// RBAC authorizer semantics against the realistic fixture in Kubism.Testing/Data/rbac.yaml.
/// The expected answers are what <c>kubectl auth can-i</c> gives for these objects by the authorizer's rules
/// (derived by hand from the documented semantics, not yet captured from a kind cluster: see tasks.md Phase 11b).
/// </summary>
public class RbacGraphTests
{
    private static readonly RbacGraph Graph = RbacFixture.Graph();

    private static KubeObject O(string json) => new(JsonNode.Parse(json)!.AsObject());

    private static bool Can(string subject, string groups, string verb, string resource, string? ns, string? name = null)
    {
        var s = RbacSubject.Parse(subject) ?? throw new InvalidOperationException(subject);
        var extra = groups.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return Graph.Explain(s, RbacRequest.Parse(verb, resource, string.IsNullOrEmpty(ns) ? null : ns, name), extra).Allowed;
    }

    // ---------- can-i table ----------

    [Theory]
    // a user bound directly in one namespace (view, aggregated)
    [InlineData("alice@corp", "", "get", "pods", "production", null, true)]
    [InlineData("alice@corp", "", "get", "pods", "staging", null, false)]
    [InlineData("alice@corp", "", "get", "pods/log", "production", null, true)]
    [InlineData("alice@corp", "", "create", "pods/exec", "production", null, false)]
    [InlineData("alice@corp", "", "list", "pods", "", null, false)]                       // all namespaces needs a ClusterRoleBinding
    // a group: edit in staging, view in production, nodes cluster-wide; RoleBinding → ClusterRole stays in its namespace
    [InlineData("erin", "devs", "get", "pods", "staging", null, true)]
    [InlineData("erin", "devs", "create", "pods/exec", "staging", null, true)]
    [InlineData("erin", "devs", "create", "pods/exec", "production", null, false)]
    [InlineData("erin", "devs", "get", "nodes", "", null, true)]
    [InlineData("erin", "devs", "delete", "nodes", "", null, false)]
    [InlineData("erin", "devs", "update", "deployments.apps/scale", "staging", null, true)]
    [InlineData("erin", "devs", "update", "deployments.apps/scale", "production", null, false)]
    [InlineData("erin", "devs", "create", "roles.rbac.authorization.k8s.io", "staging", null, false)]
    [InlineData("erin", "devs", "delete", "secrets", "production", null, false)]
    // implicit group: system:authenticated reads secrets through the legacy binding
    [InlineData("zoe", "", "get", "secrets", "staging", null, true)]
    [InlineData("zoe", "", "delete", "secrets", "staging", null, false)]
    [InlineData("system:anonymous", "", "get", "secrets", "staging", null, false)]
    [InlineData("zoe", "", "create", "selfsubjectaccessreviews", "", null, true)]
    // admin in one namespace (aggregated, includes rbac roles)
    [InlineData("gina", "prod-ops", "create", "rolebindings.rbac.authorization.k8s.io", "production", null, true)]
    [InlineData("gina", "prod-ops", "create", "rolebindings.rbac.authorization.k8s.io", "staging", null, false)]
    // service account with resourceNames
    [InlineData("system:serviceaccount:staging:ci-bot", "", "patch", "deployments", "staging", null, true)]
    [InlineData("system:serviceaccount:staging:ci-bot", "", "patch", "deployments", "production", null, false)]
    [InlineData("system:serviceaccount:staging:ci-bot", "", "update", "deployments/scale", "staging", null, true)]
    [InlineData("system:serviceaccount:staging:ci-bot", "", "get", "configmaps", "staging", "app-config", true)]
    [InlineData("system:serviceaccount:staging:ci-bot", "", "get", "configmaps", "staging", "other", false)]
    [InlineData("system:serviceaccount:staging:ci-bot", "", "list", "configmaps", "staging", null, false)]  // a request without a name never matches resourceNames
    // a Role with a subresource
    [InlineData("bob", "", "create", "pods/exec", "production", null, true)]
    [InlineData("bob", "", "create", "pods/exec", "staging", null, false)]
    [InlineData("bob", "", "get", "pods", "production", null, true)]
    [InlineData("bob", "", "create", "pods", "production", null, false)]
    // escalation verbs
    [InlineData("dave", "", "bind", "clusterroles.rbac.authorization.k8s.io", "", null, true)]
    [InlineData("dave", "", "impersonate", "users", "", null, true)]
    [InlineData("dave", "", "get", "pods", "default", null, false)]
    // binding to a role that does not exist grants nothing
    [InlineData("carol", "", "get", "pods", "default", null, false)]
    // cluster-admin in one namespace: not cluster-scoped resources, not non-resource URLs, not all namespaces
    [InlineData("intern", "", "create", "pods", "staging", null, true)]
    [InlineData("intern", "", "create", "pods", "production", null, false)]
    [InlineData("intern", "", "get", "nodes", "", null, false)]
    [InlineData("intern", "", "get", "/metrics", "", null, false)]
    [InlineData("intern", "", "get", "pods", "", null, false)]
    // cluster-admin cluster-wide, with non-resource URLs
    [InlineData("root", "", "delete", "nodes", "", null, true)]
    [InlineData("root", "", "get", "/metrics", "", null, true)]
    [InlineData("root", "", "delete", "secrets", "production", null, true)]
    // non-resource URLs: exact, prefix wildcard, other verbs
    [InlineData("anyone", "monitoring", "get", "/metrics", "", null, true)]
    [InlineData("anyone", "monitoring", "get", "/healthz/ping", "", null, true)]
    [InlineData("anyone", "monitoring", "get", "/version", "", null, true)]                // public-info-viewer, through system:authenticated
    [InlineData("anyone", "monitoring", "post", "/metrics", "", null, false)]
    [InlineData("system:serviceaccount:monitoring:prometheus", "", "get", "/metrics", "", null, true)]
    [InlineData("system:anonymous", "", "get", "/version", "", null, true)]                // system:unauthenticated
    [InlineData("system:anonymous", "", "get", "/metrics", "", null, false)]
    // service accounts are in system:serviceaccounts:<their namespace>
    [InlineData("system:serviceaccount:default:app", "", "get", "pods", "default", null, true)]
    [InlineData("system:serviceaccount:default:app", "", "get", "pods", "staging", null, false)]
    [InlineData("system:serviceaccount:other:app", "", "get", "pods", "default", null, false)]
    // the group system:masters is always authorized
    [InlineData("anyone", "system:masters", "delete", "nodes", "", null, true)]
    public void Explain_matches_the_authorizer(string subject, string groups, string verb, string resource, string ns, string? name, bool expected) =>
        Assert.Equal(expected, Can(subject, groups, verb, resource, ns, name));

    // ---------- rule matching ----------

    [Theory]
    [InlineData("pods", null, "pods", true)]
    [InlineData("pods", "log", "pods", false)]                 // a plain resource does not cover its subresources
    [InlineData("pods/log", "log", "pods", true)]
    [InlineData("pods/*", "exec", "pods", true)]
    [InlineData("pods/*", null, "pods", false)]
    [InlineData("*/scale", "scale", "deployments", true)]
    [InlineData("*/scale", "scale", "statefulsets", true)]
    [InlineData("*/scale", "status", "deployments", false)]
    [InlineData("*/scale", null, "deployments", false)]
    [InlineData("*", "exec", "pods", true)]                    // * covers subresources too
    [InlineData("*", null, "anything", true)]
    public void Resource_matching_follows_the_authorizer(string ruleResource, string? sub, string resource, bool expected)
    {
        var rule = new RbacRule(["*"], [ruleResource], ["get"], [], []);
        Assert.Equal(expected, RbacGraph.ResourceMatches(rule, resource, sub));
    }

    [Fact]
    public void Wildcard_verbs_and_groups_match_and_exact_values_do_not_over_match()
    {
        var any = new RbacRule(["*"], ["pods"], ["*"], [], []);
        Assert.True(RbacGraph.RuleMatches(any, new RbacRequest("deletecollection", "", "pods")));
        Assert.True(RbacGraph.RuleMatches(any, new RbacRequest("get", "apps", "pods")));
        var core = new RbacRule([""], ["pods"], ["get"], [], []);
        Assert.True(RbacGraph.RuleMatches(core, new RbacRequest("get", "", "pods")));
        Assert.False(RbacGraph.RuleMatches(core, new RbacRequest("get", "apps", "pods")));
        Assert.False(RbacGraph.RuleMatches(core, new RbacRequest("list", "", "pods")));
        Assert.True(RbacGraph.RuleMatches(core, new RbacRequest("get", null, "pods")));      // group unspecified: any group
    }

    // ---------- aggregation ----------

    [Fact]
    public void Aggregated_cluster_roles_get_the_rules_of_the_roles_their_selectors_match()
    {
        var view = Graph.FindRole("ClusterRole", null, "view")!;
        var edit = Graph.FindRole("ClusterRole", null, "edit")!;
        var admin = Graph.FindRole("ClusterRole", null, "admin")!;
        Assert.True(view.Aggregated);
        Assert.DoesNotContain(view.Rules, r => r.Resources.Contains("secrets"));
        Assert.Contains(view.Rules, r => r.Resources.Contains("pods/log"));
        Assert.Contains(edit.Rules, r => r.Resources.Contains("secrets"));
        Assert.Contains(edit.Rules, r => r.Resources.Contains("pods"));          // through view (aggregate-to-edit label), recursively
        Assert.Contains(admin.Rules, r => r.Resources.Contains("rolebindings"));
        Assert.Contains(admin.Rules, r => r.Resources.Contains("deployments/scale"));
    }

    [Fact]
    public void Aggregation_cycles_terminate_and_selector_expressions_work()
    {
        var a = O("""{"kind":"ClusterRole","metadata":{"name":"a","labels":{"x":"1"}},"aggregationRule":{"clusterRoleSelectors":[{"matchLabels":{"x":"1"}}]},"rules":[{"apiGroups":[""],"resources":["pods"],"verbs":["get"]}]}""");
        var b = O("""{"kind":"ClusterRole","metadata":{"name":"b","labels":{"x":"1","tier":"gold"}},"aggregationRule":{"clusterRoleSelectors":[{"matchExpressions":[{"key":"x","operator":"Exists"}]}]},"rules":[{"apiGroups":[""],"resources":["services"],"verbs":["get"]}]}""");
        var c = O("""{"kind":"ClusterRole","metadata":{"name":"c","labels":{"tier":"silver"}},"aggregationRule":{"clusterRoleSelectors":[{"matchExpressions":[{"key":"tier","operator":"NotIn","values":["gold"]}]}]},"rules":[]}""");
        var g = RbacGraph.Build([], [a, b, c], [], []);
        Assert.Equal(2, g.FindRole("ClusterRole", null, "a")!.Rules.Count);      // itself + b, no infinite loop
        Assert.Equal(2, g.FindRole("ClusterRole", null, "b")!.Rules.Count);
        // NotIn [gold] also matches a role without the label (a), and a's own selector then pulls in b
        Assert.Equal(2, g.FindRole("ClusterRole", null, "c")!.Rules.Count);
        Assert.Null(g.FindRole("ClusterRole", null, "missing"));
    }

    // ---------- orphans, scope, who can ----------

    [Fact]
    public void A_binding_to_a_missing_role_is_reported_and_explained()
    {
        var orphan = Assert.Single(Graph.OrphanBindings);
        Assert.Equal("old-role", orphan.Name);
        Assert.Equal("removed-role", orphan.RoleName);
        var ex = Graph.Explain(new RbacSubject("User", "carol"), RbacRequest.Parse("get", "pods", "default"));
        Assert.Contains(ex.Notes, n => n.Contains("removed-role") && n.Contains("does not exist"));
    }

    [Fact]
    public void A_role_binding_to_a_cluster_role_is_marked_and_scoped_to_its_namespace()
    {
        var b = Graph.Bindings.Single(x => x.Name == "devs-edit");
        Assert.True(b.ClusterRoleInNamespace);
        Assert.Equal("staging", b.Namespace);
        var ex = Graph.Explain(new RbacSubject("Group", "devs"), RbacRequest.Parse("create", "pods/exec", "staging"));
        Assert.All(ex.Paths, p => Assert.Contains("staging only", p.Note));
        Assert.All(ex.Paths, p => Assert.Equal("devs-edit", p.Binding.Name));
    }

    [Fact]
    public void WhoCan_lists_subjects_with_their_paths()
    {
        var secrets = Graph.WhoCan(RbacRequest.Parse("get", "secrets", "staging")).Select(e => e.Subject.Key).ToHashSet();
        Assert.Contains("Group:devs", secrets);
        Assert.Contains("Group:system:authenticated", secrets);
        Assert.Contains("Group:platform-admins", secrets);
        Assert.Contains("User:root", secrets);
        Assert.Contains("User:intern", secrets);
        Assert.Contains("Group:system:masters", secrets);
        Assert.DoesNotContain("User:bob", secrets);
        Assert.DoesNotContain("Group:prod-ops", secrets);                         // admin in production only
        var prod = Graph.WhoCan(RbacRequest.Parse("get", "secrets", "production")).Select(e => e.Subject.Key).ToHashSet();
        Assert.Contains("Group:prod-ops", prod);
        Assert.DoesNotContain("User:intern", prod);

        var broad = Graph.WhoCan(RbacRequest.Parse("get", "secrets", "staging")).Single(e => e.Subject.Name == "system:authenticated");
        Assert.True(broad.IsBroad);
        Assert.Contains(broad.Paths, p => p.Binding.Name == "legacy-open-secrets" && p.Role.Name == "secret-reader");
    }

    [Fact]
    public void WhoCan_without_a_namespace_only_counts_cluster_wide_bindings()
    {
        var keys = Graph.WhoCan(RbacRequest.Parse("get", "pods", null)).Select(e => e.Subject.Key).ToHashSet();
        Assert.Contains("User:root", keys);
        Assert.DoesNotContain("Group:devs", keys);
        Assert.DoesNotContain("User:intern", keys);
    }

    [Fact]
    public void Explain_names_the_implicit_group_a_right_comes_through()
    {
        var ex = Graph.Explain(new RbacSubject("User", "zoe"), RbacRequest.Parse("get", "secrets", "staging"));
        var p = Assert.Single(ex.Paths);
        Assert.Equal("system:authenticated", p.ViaGroup);
        Assert.Equal("legacy-open-secrets", p.Binding.Name);
        var sa = Graph.Explain(RbacSubject.Parse("system:serviceaccount:default:app")!, RbacRequest.Parse("get", "pods", "default"));
        Assert.Contains(sa.Paths, x => x.ViaGroup == "system:serviceaccounts:default");
    }

    [Fact]
    public void RightsOf_collects_paths_and_the_namespaces_they_apply_to()
    {
        var r = Graph.RightsOf(new RbacSubject("User", "erin"), ["devs"]);
        Assert.Equal(["production", "staging"], r.Namespaces);
        Assert.Contains(r.Paths, p => p.Binding.Namespace is null && p.Binding.Name == "node-readers");
        Assert.Contains(r.Paths, p => p.Binding.Name == "legacy-open-secrets");
        Assert.Empty(r.Notes);
        var carol = Graph.RightsOf(new RbacSubject("User", "carol"));
        Assert.Contains(carol.Notes, n => n.Contains("removed-role"));
    }

    // ---------- matrix ----------

    [Fact]
    public void Matrix_shows_verbs_by_resource_per_namespace_with_named_objects_limited_and_risky_cells_marked()
    {
        var staging = Graph.MatrixFor(new RbacSubject("Group", "devs"), "staging");
        RightCell Cell(RightsMatrix m, string row, string verb) => m.Rows.Single(r => r.Resource == row).Cells[m.Verbs.ToList().IndexOf(verb)];
        Assert.Equal(RightLevel.Allowed, Cell(staging, "pods/exec", "create").Level);
        Assert.True(Cell(staging, "pods/exec", "create").Risky);
        Assert.True(Cell(staging, "secrets", "get").Risky);
        Assert.False(Cell(staging, "secrets", "create").Risky);
        Assert.Equal(RightLevel.None, Cell(staging, "secrets", "deletecollection").Level);
        Assert.Contains("devs-edit", Cell(staging, "pods", "create").Via);

        var cluster = Graph.MatrixFor(new RbacSubject("Group", "devs"), null);
        Assert.Equal(RightLevel.Allowed, Cell(cluster, "nodes", "list").Level);
        Assert.DoesNotContain(cluster.Rows, r => r.Resource == "pods/exec");

        var ci = Graph.MatrixFor(RbacSubject.Parse("system:serviceaccount:staging:ci-bot")!, "staging");
        Assert.Equal(RightLevel.Limited, Cell(ci, "configmaps", "get").Level);
        Assert.Equal(RightLevel.Allowed, Cell(ci, "deployments.apps", "patch").Level);

        var admin = Graph.MatrixFor(new RbacSubject("User", "root"), null);
        Assert.True(Cell(admin, "*", "delete").Risky);
    }

    // ---------- risks ----------

    [Fact]
    public void Risk_flags_cover_wildcards_escalation_exec_secrets_pods_and_cluster_admin()
    {
        string[] Codes(string role) => RbacGraph.RisksOf(Graph.FindRole("ClusterRole", null, role)!).Select(r => r.Code).ToArray();
        Assert.Contains("wildcard-all", Codes("cluster-admin"));
        Assert.Contains("cluster-admin", Codes("cluster-admin"));
        Assert.Contains("read-secrets", Codes("secret-reader"));
        var granter = Codes("role-granter");
        Assert.Contains("bind", granter); Assert.Contains("escalate", granter); Assert.Contains("impersonate", granter);
        var edit = Codes("edit");
        Assert.Contains("exec-pods", edit); Assert.Contains("attach-pods", edit); Assert.Contains("read-secrets", edit); Assert.Contains("create-pods", edit);
        Assert.Empty(Codes("view"));
        Assert.Empty(Codes("node-reader"));

        // a wildcard verb on pods includes create pods and escalation-like power on pods/exec only when the resource matches
        var wild = new RbacRule([""], ["pods"], ["*"], [], []);
        var codes = RbacGraph.RisksOf(wild).Select(r => r.Code).ToList();
        Assert.Contains("wildcard-verbs", codes); Assert.Contains("create-pods", codes); Assert.DoesNotContain("exec-pods", codes);
        var star = new RbacRule([""], ["pods/*"], ["create"], [], []);
        Assert.Contains("exec-pods", RbacGraph.RisksOf(star).Select(r => r.Code));
        var named = new RbacRule([""], ["secrets"], ["get"], ["one"], []);
        Assert.Equal(FindingSeverity.Low, RbacGraph.RisksOf(named).Single(r => r.Code == "read-secrets").Severity);
    }

    [Fact]
    public void Binding_risks_flag_over_broad_subjects_and_cluster_admin()
    {
        string[] Codes(string name) => RbacGraph.RisksOf(Graph.Bindings.Single(b => b.Name == name)).Select(r => r.Code).ToArray();
        Assert.Contains("authenticated-subject", Codes("legacy-open-secrets"));
        Assert.Contains("cluster-admin", Codes("cluster-admins"));
        Assert.Contains("cluster-admin", Codes("intern-admin"));
        Assert.Empty(Codes("system:basic-user"));
        Assert.Empty(Codes("system:public-info-viewer"));          // anonymous may read public info
        Assert.Empty(Codes("devs-view"));

        var open = RbacGraph.Build([], [O("""{"kind":"ClusterRole","metadata":{"name":"cr"},"rules":[]}""")], [],
            [O("""{"kind":"ClusterRoleBinding","metadata":{"name":"anon"},"roleRef":{"kind":"ClusterRole","name":"cr"},"subjects":[{"kind":"User","name":"system:anonymous"}]}""")]);
        Assert.Contains("anonymous-subject", RbacGraph.RisksOf(open.Bindings[0]).Select(r => r.Code));
    }

    // ---------- incomplete data ----------

    [Fact]
    public void Missing_kinds_are_reported_in_every_answer()
    {
        var status = new RbacKindStatus[] { new("roles", true, null), new("clusterroles", true, null), new("rolebindings", false, "rolebindings is forbidden"), new("clusterrolebindings", true, null) };
        var g = RbacFixture.Graph(skip: o => o.Kind == "RoleBinding", availability: status);
        Assert.False(g.IsComplete);
        Assert.Equal(["rolebindings"], g.MissingKinds);
        Assert.Contains("Incomplete data", g.IncompleteWarning);
        Assert.Contains("rolebindings is forbidden", g.IncompleteWarning);
        Assert.Contains(g.Explain(new RbacSubject("Group", "devs"), RbacRequest.Parse("get", "pods", "staging")).Notes, n => n.Contains("Incomplete data"));
        Assert.Contains(g.RightsOf(new RbacSubject("Group", "devs")).Notes, n => n.Contains("Incomplete data"));
        Assert.NotNull(RbacChain.Build(g).Warning);
        Assert.True(Graph.IsComplete);
        Assert.Null(RbacChain.Build(Graph).Warning);
    }

    // ---------- subject parsing and request parsing ----------

    [Theory]
    [InlineData("ServiceAccount staging/ci-bot", "ServiceAccount", "ci-bot", "staging")]
    [InlineData("staging/ci-bot", "ServiceAccount", "ci-bot", "staging")]
    [InlineData("system:serviceaccount:staging:ci-bot", "ServiceAccount", "ci-bot", "staging")]
    [InlineData("User alice@corp", "User", "alice@corp", null)]
    [InlineData("Group system:masters", "Group", "system:masters", null)]
    [InlineData("alice", "User", "alice", null)]
    public void Subjects_parse_in_the_forms_users_type(string text, string kind, string name, string? ns)
    {
        var s = RbacSubject.Parse(text)!;
        Assert.Equal((kind, name, ns), (s.Kind, s.Name, s.Namespace));
    }

    [Fact]
    public void Requests_parse_resource_group_and_subresource()
    {
        var r = RbacRequest.Parse("update", "deployments.apps/scale", "staging", "web");
        Assert.Equal(("deployments", "apps", "scale", "web", "staging"), (r.Resource, r.Group, r.Subresource, r.Name, r.Namespace));
        Assert.Equal("", RbacRequest.Parse("get", "pods").Group);                       // well known: core
        Assert.Null(RbacRequest.Parse("get", "widgets").Group);                         // unknown: any group
        Assert.True(RbacRequest.Parse("get", "/healthz").IsNonResource);
    }

    // ---------- chain and layout ----------

    [Fact]
    public void Chain_draws_subject_binding_role_rules_and_marks_the_cluster_role_in_a_namespace()
    {
        var chain = RbacChain.Build(Graph);
        Assert.Contains(chain.Edges, e => e.Highlight && e.From == "b:RoleBinding/staging/devs-edit" && e.To == "r:ClusterRole//edit" && e.Label.Contains("staging only"));
        var missing = Assert.Single(chain.Nodes, n => n.Missing && n.Kind == RbacNodeKind.Role);
        Assert.Equal("removed-role", missing.Label);
        Assert.Contains(chain.Edges, e => e.To == missing.Id && e.Highlight);
        Assert.All(chain.Nodes, n => Assert.Equal((int)n.Kind, n.Column));
        // one place per node, rows are dense per column
        Assert.Equal(chain.Nodes.Count, chain.Nodes.Select(n => (n.Column, n.Row)).Distinct().Count());
        foreach (var col in chain.Nodes.GroupBy(n => n.Column)) Assert.Equal(Enumerable.Range(0, col.Count()), col.Select(n => n.Row).Order());
        Assert.True(chain.Nodes.All(n => n.Row < chain.MaxRows));
        var rules = chain.Nodes.Single(n => n.Id == "u:ClusterRole//role-granter");
        Assert.True(rules.Risky);
        Assert.Equal(2, rules.Rules.Count);
    }

    [Fact]
    public void Chain_filters_by_namespace_subject_kind_risk_and_focus()
    {
        var staging = RbacChain.Build(Graph, new RbacChainFilter { Namespace = "staging", IncludeCluster = false });
        Assert.All(staging.Nodes.Where(n => n.Kind == RbacNodeKind.Binding), n => Assert.Equal("staging", n.Scope));
        Assert.DoesNotContain(staging.Nodes, n => n.Label == "devs-view");

        // under a namespace filter, a cluster-wide binding only appears for the subjects that belong to that namespace
        var monitoring = RbacChain.Build(Graph, new RbacChainFilter { Namespace = "monitoring" });
        Assert.Contains(monitoring.Nodes, n => n.Label == "monitoring-metrics");
        Assert.Equal(["monitoring/prometheus"], monitoring.Nodes.Where(n => n.Kind == RbacNodeKind.Subject).Select(n => n.Label));
        Assert.DoesNotContain(monitoring.Nodes, n => n.Label is "cluster-admins" or "node-readers" or "legacy-open-secrets");
        var other = RbacChain.Build(Graph, new RbacChainFilter { Namespace = "nothing-here", SubjectKinds = new HashSet<string> { "ServiceAccount" } });
        Assert.Empty(other.Nodes);

        var sas = RbacChain.Build(Graph, new RbacChainFilter { SubjectKinds = new HashSet<string> { "ServiceAccount" } });
        Assert.All(sas.Nodes.Where(n => n.Kind == RbacNodeKind.Subject), n => Assert.Equal("ServiceAccount", n.Detail));

        var risky = RbacChain.Build(Graph, new RbacChainFilter { RiskyOnly = true });
        var all = RbacChain.Build(Graph);
        Assert.True(risky.Nodes.Count < all.Nodes.Count);
        Assert.Contains(risky.Nodes, n => n.Label == "legacy-open-secrets");
        Assert.DoesNotContain(risky.Nodes, n => n.Label == "node-readers");

        var search = RbacChain.Build(Graph, new RbacChainFilter { SubjectQuery = "alice" });
        Assert.Single(search.Nodes, n => n.Kind == RbacNodeKind.Subject);

        var focus = RbacChain.Build(Graph, new RbacChainFilter { Focus = new RbacSubject("Group", "devs") });
        Assert.Contains(focus.Nodes, n => n.Label == "devs-edit");
        Assert.DoesNotContain(focus.Nodes, n => n.Label == "exec");

        var capped = RbacChain.Build(Graph, new RbacChainFilter { MaxBindings = 3 });
        Assert.True(capped.Truncated);
        Assert.Equal(3, capped.Nodes.Count(n => n.Kind == RbacNodeKind.Binding));
    }

    [Fact]
    public void Layout_orders_rows_so_connected_nodes_sit_together()
    {
        // two independent chains must not interleave: each subject sits at the row of its binding's neighbourhood
        var g = RbacGraph.Build([], [O("""{"kind":"ClusterRole","metadata":{"name":"r1"},"rules":[{"apiGroups":[""],"resources":["pods"],"verbs":["get"]}]}"""), O("""{"kind":"ClusterRole","metadata":{"name":"r2"},"rules":[{"apiGroups":[""],"resources":["nodes"],"verbs":["get"]}]}""")], [],
            [O("""{"kind":"ClusterRoleBinding","metadata":{"name":"b1"},"roleRef":{"kind":"ClusterRole","name":"r1"},"subjects":[{"kind":"User","name":"z"}]}"""),
             O("""{"kind":"ClusterRoleBinding","metadata":{"name":"b2"},"roleRef":{"kind":"ClusterRole","name":"r2"},"subjects":[{"kind":"User","name":"a"}]}""")]);
        var chain = RbacChain.Build(g);
        int Row(string id) => chain.Nodes.Single(n => n.Id == id).Row;
        // without ordering by neighbours, subject "a" (alphabetical first) would be row 0 against b1 at row 0
        Assert.Equal(Row("s:User:a"), Row("b:ClusterRoleBinding//b2"));
        Assert.Equal(Row("s:User:z"), Row("b:ClusterRoleBinding//b1"));
        Assert.Equal(Row("b:ClusterRoleBinding//b1"), Row("r:ClusterRole//r1"));
        Assert.Equal(Row("b:ClusterRoleBinding//b2"), Row("u:ClusterRole//r2"));
    }
}
