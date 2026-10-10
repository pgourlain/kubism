using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Exec;
using Kubism.Core.Forward;
using Kubism.Core.Logs;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.ViewModels;

/// <summary>One connected cluster: sidebar, current view, details panel, dock. Owns everything it starts.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase, IClosable
{
    private readonly Timer _ageTimer;
    private LocalSampler? _sampler;
    private bool _disposed;

    public WorkspaceViewModel(MainViewModel main, ClusterSession session)
    {
        Main = main; Session = session;
        Namespaces = new NamespaceSelectorViewModel(session, main.Settings, main.Ui);
        Details = new DetailsViewModel(this);
        Dock = new DockViewModel();
        BuildNav();
        session.Discovery.Changed += () => Ui.Post(BuildNav);
        _ageTimer = new Timer(_ => Ui.Post(() => (CurrentContent as ResourceListViewModel)?.TickAges()), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        if (session.Metadata.LocalSampling)
        {
            _sampler = new LocalSampler(session.Entry.Id, session.Metrics, main.UsageStore);
            _sampler.Start();
        }
        _ = RestoreKeptForwardsAsync();
        var overview = FindNav(NavTarget.Overview);
        if (overview is not null) Navigate(overview);
        _ = DimForbiddenAsync();
    }

    public MainViewModel Main { get; }
    public ClusterSession Session { get; }
    public IUiThread Ui => Main.Ui;
    public IDialogService Dialogs => Main.Dialogs;
    public IClipboardService Clipboard => Main.Clipboard;
    public NamespaceSelectorViewModel Namespaces { get; }
    public DetailsViewModel Details { get; }
    public DockViewModel Dock { get; }
    public ObservableCollection<NavNode> NavRoots { get; } = [];

    /// <summary>Secondary (split) pane: no sidebar, to leave room for the list.</summary>
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private NavNode? _selectedNav;
    [ObservableProperty] private object? _currentContent;

    public string Id => Session.Entry.Id;
    public string Title => string.IsNullOrWhiteSpace(Session.Metadata.Label) ? Session.Entry.ContextName : Session.Metadata.Label!;
    public string Color => Main.Clusters.FirstOrDefault(c => c.Id == Id)?.Color ?? "#3B82F6";
    public SafetyMode SafetyMode => Session.Metadata.SafetyMode;
    public bool IsProduction => SafetyMode == SafetyMode.Production;
    public bool IsReadOnly => SafetyMode == SafetyMode.ReadOnly;
    public string BannerText => SafetyMode switch
    {
        SafetyMode.Production => $"PRODUCTION · {Title}: destructive actions need the resource name typed",
        SafetyMode.ReadOnly => $"READ-ONLY · {Title}: all changes are refused",
        _ => "",
    };
    public bool HasBanner => SafetyMode != SafetyMode.Normal;
    public string StateText => Session.State.ToString();
    public bool IsDegraded => Session.State == SessionState.Degraded;
    public string? ServerVersion => Session.ServerVersion;

    public void RefreshHeader()
    {
        foreach (var p in new[] { nameof(Title), nameof(Color), nameof(SafetyMode), nameof(IsProduction), nameof(IsReadOnly), nameof(BannerText), nameof(HasBanner), nameof(StateText), nameof(IsDegraded) })
            OnPropertyChanged(p);
    }

    // ---------- navigation ----------

    private static readonly (string section, string[] kinds)[] Fixed =
    [
        ("Workloads", ["pods", "deployments", "daemonsets", "statefulsets", "replicasets", "jobs", "cronjobs"]),
        ("Config", ["configmaps", "secrets", "resourcequotas", "limitranges", "horizontalpodautoscalers", "poddisruptionbudgets"]),
        ("Network", ["services", "endpoints", "endpointslices", "ingresses", "ingressclasses", "networkpolicies"]),
        ("Storage", ["persistentvolumeclaims", "persistentvolumes", "storageclasses"]),
        ("Access Control", ["serviceaccounts", "roles", "rolebindings", "clusterroles", "clusterrolebindings"]),
    ];

    private static readonly HashSet<string> BuiltinGroups = ["", "apps", "batch", "policy", "autoscaling", "networking.k8s.io", "storage.k8s.io", "rbac.authorization.k8s.io", "apiextensions.k8s.io",
        "admissionregistration.k8s.io", "apiregistration.k8s.io", "authentication.k8s.io", "authorization.k8s.io", "certificates.k8s.io", "coordination.k8s.io", "discovery.k8s.io", "events.k8s.io",
        "flowcontrol.apiserver.k8s.io", "node.k8s.io", "scheduling.k8s.io", "resource.k8s.io", "metrics.k8s.io"];

    public void BuildNav()
    {
        var selectedTitle = SelectedNav?.Title;
        var kinds = Session.Discovery.Kinds.Where(k => k.CanList).ToList();
        ResourceKind? Find(string resource) => kinds.FirstOrDefault(k => k.Resource == resource && BuiltinGroups.Contains(k.Group));
        NavRoots.Clear();
        NavRoots.Add(new NavNode("Overview".T(), NavTarget.Overview, null, "overview"));
        NavRoots.Add(new NavNode("Right-sizing".T(), NavTarget.RightSizing, null, "rightsizing"));
        if (Find("nodes") is { } nodes) NavRoots.Add(new NavNode("Nodes".T(), NavTarget.Kind, nodes, "node"));
        foreach (var (section, names) in Fixed)
        {
            var g = new NavNode(section.T());
            foreach (var n in names)
                if (Find(n) is { } k) g.Children.Add(new NavNode(Plural(k.Kind), NavTarget.Kind, k));
            if (g.Children.Count > 0) NavRoots.Add(g);
        }
        if (Find("namespaces") is { } ns) NavRoots.Add(new NavNode("Namespaces".T(), NavTarget.Kind, ns, "namespaces"));
        if (Find("events") is { } ev) NavRoots.Add(new NavNode("Events".T(), NavTarget.Kind, ev, "events"));
        NavRoots.Add(new NavNode("Helm".T(), NavTarget.Helm, null, "helm"));
        NavRoots.Add(new NavNode("Compare".T(), NavTarget.Compare, null, "compare"));
        NavRoots.Add(new NavNode("Security".T(), NavTarget.Security, null, "security"));
        var custom = kinds.Where(k => !BuiltinGroups.Contains(k.Group)).GroupBy(k => k.Group).OrderBy(g => g.Key).ToList();
        var cr = new NavNode("Custom Resources".T()) { IsExpanded = false };
        cr.Children.Add(new NavNode("Definitions (CRDs)", NavTarget.Kind, Find("customresourcedefinitions")));
        foreach (var g in custom)
        {
            var group = new NavNode(g.Key) { IsExpanded = false };
            foreach (var k in g.OrderBy(k => k.Kind)) group.Children.Add(new NavNode(k.Kind, NavTarget.Kind, k));
            cr.Children.Add(group);
        }
        NavRoots.Add(cr);
        var all = new NavNode("All kinds".T()) { IsExpanded = false };
        foreach (var k in kinds.OrderBy(k => k.Kind).ThenBy(k => k.Group)) all.Children.Add(new NavNode(k.Group.Length == 0 ? k.Kind : $"{k.Kind} ({k.Group})", NavTarget.Kind, k));
        NavRoots.Add(all);
        NavRoots.Add(new NavNode("Create from YAML…", NavTarget.CreateYaml, null, "create"));
        NavRoots.Add(new NavNode("Activity".T(), NavTarget.Activity, null, "activity"));
        NavRoots.Add(new NavNode("Port forwards".T(), NavTarget.PortForwards, null, "portforward"));
        if (selectedTitle is not null) SelectedNav = Flatten(NavRoots).FirstOrDefault(n => n.Title == selectedTitle && n.Target != NavTarget.Group);
    }

    private static string Plural(string kind) => kind switch
    {
        "Endpoints" => "Endpoints", "NetworkPolicy" => "Network Policies", "Ingress" => "Ingresses", "IngressClass" => "Ingress Classes", "StorageClass" => "Storage Classes",
        "PodDisruptionBudget" => "Pod Disruption Budgets", "HorizontalPodAutoscaler" => "HPAs", "EndpointSlice" => "Endpoint Slices", "ResourceQuota" => "Resource Quotas",
        "LimitRange" => "Limit Ranges", "PersistentVolumeClaim" => "Persistent Volume Claims", "PersistentVolume" => "Persistent Volumes", "ClusterRole" => "Cluster Roles",
        "ClusterRoleBinding" => "Cluster Role Bindings", "RoleBinding" => "Role Bindings", "ServiceAccount" => "Service Accounts", "ConfigMap" => "Config Maps", "StatefulSet" => "Stateful Sets",
        "DaemonSet" => "Daemon Sets", "ReplicaSet" => "Replica Sets", "CronJob" => "Cron Jobs",
        _ => kind.EndsWith("s") ? kind : kind.EndsWith("y") ? kind[..^1] + "ies" : kind + "s",
    };

    public static IEnumerable<NavNode> Flatten(IEnumerable<NavNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    public NavNode? FindNav(NavTarget t) => Flatten(NavRoots).FirstOrDefault(n => n.Target == t);

    private async Task DimForbiddenAsync()
    {
        try
        {
            if (!Main.Settings.Current.DimForbiddenKinds) return;
            foreach (var n in Flatten(NavRoots).Where(n => n.Target == NavTarget.Kind && n.Kind is { } k && !BuiltinGroups.Skip(1).Contains(k.Group) || n.Kind is { Group: "" or "apps" or "batch" }).ToList())
            {
                if (_disposed || n.Kind is null) return;
                var r = await Session.Access.CanAsync("list", n.Kind.Gvr, null, null, Session.SessionToken);
                if (!r.Allowed) Ui.Post(() => n.IsDimmed = true);
            }
        }
        catch (Exception) { /* dimming is cosmetic */ }
    }

    partial void OnSelectedNavChanged(NavNode? value)
    {
        if (value is null || value.Target == NavTarget.Group) return;
        if (CurrentContent is ResourceListViewModel cur && value.Kind is not null && cur.Kind == value.Kind) return;
        _ = SwapContentAsync(value);
    }

    public void Navigate(NavNode node)
    {
        if (node.Target == NavTarget.Group) { node.IsExpanded = !node.IsExpanded; return; }
        SelectedNav = node;
    }

    public void OpenKind(ResourceKind kind)
    {
        var node = Flatten(NavRoots).FirstOrDefault(n => n.Kind == kind && n.Target == NavTarget.Kind);
        if (node is null) { node = new NavNode(kind.Kind, NavTarget.Kind, kind); }
        SelectedNav = node;
    }

    private async Task SwapContentAsync(NavNode node)
    {
        if (node.Target == NavTarget.PortForwards)
        {
            // Port forwards live in the bottom dock: show/focus that tab and leave the centre view alone.
            // (Making the dock item the centre content got it disposed when navigating away: it stopped updating.)
            EnsurePortForwards();
            return;
        }
        var old = CurrentContent;
        Details.Close();
        CurrentContent = node.Target switch
        {
            NavTarget.Overview => new OverviewViewModel(this),
            NavTarget.Kind when node.Kind is not null => NewList(node.Kind),
            NavTarget.RightSizing => new RightSizingViewModel(this),
            NavTarget.Helm => new HelmViewModel(this),
            NavTarget.Activity => new ActivityViewModel(this),
            NavTarget.CreateYaml => new CreateYamlViewModel(this),
            NavTarget.Compare => new CompareViewModel(this),
            NavTarget.Security => new SecurityViewModel(this),
            _ => null,
        };
        // Dock items own their lifetime (the dock closes them); never dispose one from here.
        if (old is IClosable c and not DockItemViewModel && !ReferenceEquals(old, CurrentContent)) await c.DisposeAsync();
    }

    private ResourceListViewModel NewList(ResourceKind kind)
    {
        var list = new ResourceListViewModel(this, kind);
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ResourceListViewModel.SelectedRow))
            {
                if (list.SelectedRow is { } row) Details.Show(kind, row.Item.Object); else if (!Details.IsEditing) Details.Close();
            }
        };
        return list;
    }

    private PortForwardsViewModel EnsurePortForwards()
    {
        var existing = Dock.Find<PortForwardsViewModel>();
        if (existing is not null) { Dock.Selected = existing; Dock.IsOpen = true; return existing; }
        var vm = new PortForwardsViewModel(this);
        Dock.Add(vm);
        return vm;
    }

    // ---------- actions ----------

    /// <summary>
    /// Confirm (typed name in production), run through ClusterActions, show the API server's message on failure.
    /// Read-only clusters are refused here as in ClusterActions itself.
    /// </summary>
    public async Task<bool> RunActionAsync(string verb, string target, bool destructive, string expectedName, Func<string?, Task> action)
    {
        var mode = Session.Metadata.SafetyMode;
        if (mode == SafetyMode.ReadOnly)
        {
            await Dialogs.ShowErrorAsync("Read-only cluster", $"{verb} {target} is refused: this cluster is in read-only mode.");
            return false;
        }
        string? confirmation = null;
        if (destructive)
        {
            if (mode == SafetyMode.Production)
            {
                confirmation = await Dialogs.ConfirmTypedAsync($"{verb} on production", $"You are about to {verb.ToLowerInvariant()} {target} on a PRODUCTION cluster.", expectedName);
                if (confirmation is null) return false;
            }
            else if (!await Dialogs.ConfirmAsync(new ConfirmRequest(verb, $"{verb} {target}?", verb, true))) return false;
        }
        try
        {
            await action(confirmation);
            Main.SetStatus($"{verb} {target}: done");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync($"{verb} failed", ex.Message);
            return false;
        }
    }

    public async Task DeleteAsync(ResourceListViewModel list, IReadOnlyList<ResourceRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        var kind = list.Kind;
        if (rows.Count == 1)
        {
            var r = rows[0];
            await RunActionAsync("Delete", $"{kind.Kind.ToLowerInvariant()} {r.Name}", true, r.Name, async c =>
            {
                var force = r.Item.Object.DeletionTimestamp is not null && kind.Kind == "Pod" && await ConfirmForce(r);
                await Session.Actions.DeleteAsync(kind.Gvr, r.Namespace, r.Name, Session.SessionToken, confirmation: c, force: force);
            });
        }
        else
        {
            var items = rows.Select(r => (r.Namespace, r.Name)).ToList();
            await RunActionAsync("Delete", $"{rows.Count} {kind.Kind.ToLowerInvariant()} objects", true, rows.Count.ToString(), async c =>
            {
                var res = await Session.Actions.DeleteManyAsync(kind.Gvr, items, Session.SessionToken, confirmation: c);
                var failed = res.Where(x => x.error is not null).ToList();
                if (failed.Count > 0) await Dialogs.ShowTextAsync("Some deletions failed", string.Join("\n", failed.Select(f => $"{f.target}: {f.error}")));
            });
        }
    }

    private async Task<bool> ConfirmForce(ResourceRowViewModel r) =>
        await Dialogs.ConfirmAsync(new ConfirmRequest("Force delete", $"{r.Name} is stuck Terminating. Force delete removes it immediately without waiting for the kubelet (the container may keep running).", "Force delete", true));

    public IReadOnlyList<ActionItem> ActionsFor(ResourceKind kind, KubeObject o)
    {
        var list = new List<ActionItem>();
        var gvr = kind.Gvr;
        var ns = o.Namespace;
        var name = o.Name;
        list.Add(new ActionItem("Edit".T(), () => { Details.BeginEditCommand.Execute(null); return Task.CompletedTask; }, "Ctrl+E") { Verb = "update" });
        switch (kind.Kind)
        {
            case "Pod":
                list.Add(new ActionItem("Logs".T(), () => OpenPodLogsAsync(o), "L") { Verb = "get", Subresource = "log" });
                if (new Core.Views.PodView(o).Restarts > 0)
                    list.Add(new ActionItem("Logs (previous)", () => OpenPodLogsAsync(o, null, previous: true)) { Verb = "get", Subresource = "log" });
                list.Add(new ActionItem("Shell".T(), () => OpenShellAsync(o), "S") { Verb = "create", Subresource = "exec" });
                list.Add(new ActionItem("Forward port…", () => ForwardPortAsync(o)) { Verb = "create", Subresource = "portforward" });
                break;
            case "Deployment" or "StatefulSet" or "DaemonSet" or "Job" or "ReplicaSet":
                list.Add(new ActionItem("Logs".T(), () => OpenWorkloadLogsAsync(o), "L") { Verb = "get", Subresource = "log" });
                break;
        }
        if (kind.Kind is "Deployment" or "StatefulSet" or "ReplicaSet")
            list.Add(new ActionItem("Scale…".T(), () => ScaleAsync(kind, o)) { Verb = "patch", Subresource = "scale" });
        if (kind.Kind is "Deployment" or "StatefulSet" or "DaemonSet")
            list.Add(new ActionItem("Restart".T(), () => RunActionAsync("Restart", $"{kind.Kind.ToLowerInvariant()} {name}", false, name, _ => Session.Actions.RolloutRestartAsync(gvr, ns!, name, Session.SessionToken))) { Verb = "patch" });
        if (kind.Kind == "Deployment")
        {
            var paused = o.At("spec", "paused")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new ActionItem(paused ? "Resume rollout" : "Pause rollout", () => RunActionAsync(paused ? "Resume rollout" : "Pause rollout", name, false, name, _ => Session.Actions.SetRolloutPausedAsync(gvr, ns!, name, !paused, Session.SessionToken))) { Verb = "patch" });
        }
        if (kind.Kind == "Service" && (o.At("spec", "ports") as System.Text.Json.Nodes.JsonArray)?.Count > 0)
            list.Add(new ActionItem("Forward port…", () => ForwardServicePortAsync(o)) { Verb = "create", Subresource = "portforward" });
        if (kind.Kind == "CustomResourceDefinition")
        {
            var group = o.StringAt("spec", "group"); var crKind = o.StringAt("spec", "names", "kind");
            list.Add(new ActionItem("Browse instances", () =>
            {
                var k = Session.Discovery.Kinds.FirstOrDefault(x => x.Group == group && x.Kind == crKind);
                if (k is not null) OpenKind(k);
                return Task.CompletedTask;
            }));
        }
        if (kind.Kind == "Node")
        {
            var cordoned = o.At("spec", "unschedulable")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new ActionItem(cordoned ? "Uncordon".T() : "Cordon".T(), () => RunActionAsync(cordoned ? "Uncordon" : "Cordon", $"node {name}", false, name, _ => Session.Actions.SetCordonAsync(name, !cordoned, Session.SessionToken))) { Verb = "patch" });
            list.Add(new ActionItem("Drain".T() + "…", () => DrainAsync(name), destructive: true) { Verb = "delete", Subresource = "eviction" });
        }
        if (kind.Kind == "CronJob")
        {
            var suspended = o.At("spec", "suspend")?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new ActionItem("Trigger now", () => RunActionAsync("Trigger", $"cronjob {name}", false, name, _ => Session.Actions.TriggerCronJobAsync(ns!, name, Session.SessionToken))) { Verb = "create" });
            list.Add(new ActionItem(suspended ? "Resume" : "Suspend", () => RunActionAsync(suspended ? "Resume" : "Suspend", $"cronjob {name}", false, name, _ => Session.Actions.SetCronJobSuspendedAsync(ns!, name, !suspended, Session.SessionToken))) { Verb = "patch" });
        }
        if (Main.Workspaces.Count > 0 && kind.Verbs.Contains("list"))
            list.Add(new ActionItem("Compare…", () => CompareObjectAsync(kind, o)));
        list.Add(ActionItem.Separator());
        list.Add(new ActionItem("Copy name", () => Clipboard.SetTextAsync(name)));
        list.Add(new ActionItem("Delete".T(), () => DeleteOneAsync(kind, o), "Delete", destructive: true) { Verb = "delete" });
        return list;
    }

    /// <summary>Opens Compare on this object's kind and namespace, against another connected cluster (or another namespace of this one when it is the only cluster).</summary>
    /// <summary>Opens the object a graph box stands for: its list filtered on its name, or the Helm page for a release.</summary>
    public void OpenGraphNode(Core.Views.GraphNode node)
    {
        if (node.Kind == Core.Helm.HelmReleaseInfo.Kind && node.Namespace is { } hns)
        {
            var helm = FindNav(NavTarget.Helm);
            if (helm is null) return;
            Details.Close();
            Namespaces.Select(hns);
            if (SelectedNav != helm) SelectedNav = helm;
            if (CurrentContent is HelmViewModel h) h.SelectWhenLoaded(hns, node.Name);
            return;
        }
        var kind = Session.Discovery.Kinds.Where(k => k.Kind == node.Kind && k.CanList).OrderBy(k => k.Group.Length == 0 ? 0 : 1).FirstOrDefault();
        if (kind is null) return;
        if (node.Namespace is not null) Namespaces.Select(node.Namespace);
        OpenKind(kind);
        if (CurrentContent is ResourceListViewModel list) list.Filter = node.Name;
    }

    private async Task CompareObjectAsync(ResourceKind kind, KubeObject o)
    {
        var others = Main.Workspaces.Where(w => w != this).ToList();
        WorkspaceViewModel? target = this;
        if (others.Count == 1) target = others[0];
        else if (others.Count > 1)
        {
            var pick = await Dialogs.ChooseAsync("Compare with", others.Select(w => w.Title).ToList());
            if (pick is null) return;
            target = others.First(w => w.Title == pick);
        }
        var node = FindNav(NavTarget.Compare);
        if (node is null) return;
        Details.Close();
        if (SelectedNav != node) SelectedNav = node;
        else CurrentContent = new CompareViewModel(this);
        if (CurrentContent is CompareViewModel cmp) await cmp.PrefillAsync(kind, o.Namespace, o.Name, target);
    }

    private Task DeleteOneAsync(ResourceKind kind, KubeObject o)
    {
        var list = CurrentContent as ResourceListViewModel;
        var row = list?.Rows.FirstOrDefault(r => r.Uid == o.Uid) ?? new ResourceRowViewModel(new Core.Cache.CacheItem(o, null));
        return DeleteAsync(list ?? new ResourceListViewModel(this, kind), [row]);
    }

    /// <summary>Disables actions the user isn't allowed to perform (SelfSubjectAccessReview, cached 5 min) and read-only clusters.</summary>
    public async Task ApplyRbacAsync(ResourceKind kind, KubeObject o, IReadOnlyList<ActionItem> items)
    {
        foreach (var item in items)
        {
            if (item.Verb is null) continue;
            if (IsReadOnly && item.Verb is not "get")
            {
                Ui.Post(() => item.Disable("Read-only cluster: changes are refused"));
                continue;
            }
            try
            {
                var gvr = item.Subresource is "log" or "exec" or "portforward" or "eviction" ? Gvr.Pods : kind.Gvr;
                var r = await Session.Access.CanAsync(item.Verb, gvr, o.Namespace, o.Name, Session.SessionToken, item.Subresource);
                if (!r.Allowed) Ui.Post(() => item.Disable(r.Reason ?? "forbidden"));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    private async Task ScaleAsync(ResourceKind kind, KubeObject o)
    {
        var current = o.IntAt("spec", "replicas") ?? 1;
        var text = await Dialogs.PromptAsync("Scale", $"Replicas for {o.Name} (currently {current}):", current.ToString());
        if (text is null || !int.TryParse(text, out var desired) || desired < 0) return;
        await RunActionAsync("Scale", $"{kind.Kind.ToLowerInvariant()} {o.Name} to {desired}", desired == 0, o.Name,
            c => Session.Actions.ScaleAsync(kind.Gvr, o.Namespace!, o.Name, desired, Session.SessionToken, c));
    }

    private async Task DrainAsync(string node)
    {
        await RunActionAsync("Drain", $"node {node}", true, node, async c =>
        {
            var progress = new Progress<DrainProgress>(p => Main.SetStatus($"Draining {node}: {p.Pod} {p.Status} ({p.Done}/{p.Total})"));
            await Session.Actions.DrainAsync(node, new DrainOptions(), progress, Session.SessionToken, c);
        });
    }

    // ---------- logs, shell, port-forward ----------

    private async Task<string?> PickContainerAsync(KubeObject pod, bool includeInit = true)
    {
        var containers = new PodViewHelper(pod).ContainerNames(includeInit);
        if (containers.Count <= 1) return containers.FirstOrDefault();
        return await Dialogs.ChooseAsync("Container", containers);
    }

    public async Task OpenPodLogsAsync(KubeObject pod, string? container = null, bool previous = false)
    {
        container ??= await PickContainerAsync(pod);
        if (container is null && new PodViewHelper(pod).ContainerNames(true).Count > 1) return;
        var buffer = new LogBuffer(Main.Settings.Current.LogBufferLines);
        var session = new LogSession(Session.Transport, new LogOptions(pod.Namespace!, pod.Name, container, Follow: !previous, Previous: previous, TailLines: previous ? null : 1000), buffer);
        var vm = new LogViewModel(this, $"{pod.Name}{(container is null ? "" : "/" + container)}{(previous ? " (previous)" : "")}", buffer, session.DisposeAsync, false);
        Dock.Add(vm);
        session.StateChanged += st => Ui.Post(() => vm.State = st == LogState.Error ? $"error: {session.Error}" : st.ToString().ToLowerInvariant());
        session.Start(Session.SessionToken);
        vm.Rebuild();
    }

    public Task OpenWorkloadLogsAsync(KubeObject workload)
    {
        var selector = Core.Logs.WorkloadLogSession.SelectorOf(workload);
        if (selector.Count == 0)
        {
            // Jobs and others without matchLabels: use the job-name / controller labels.
            selector = workload.Kind == "Job" ? new Dictionary<string, string> { ["job-name"] = workload.Name } : selector;
        }
        var buffer = new LogBuffer(Main.Settings.Current.LogBufferLines);
        var wl = new WorkloadLogSession(Session.Transport, Session.Cache, workload.Namespace!, selector, buffer);
        var vm = new LogViewModel(this, $"{workload.Kind.ToLowerInvariant()}/{workload.Name}", buffer, wl.DisposeAsync, true);
        Dock.Add(vm);
        wl.Start(Session.SessionToken);
        return Task.CompletedTask;
    }

    public async Task OpenShellAsync(KubeObject pod, string? container = null)
    {
        container ??= await PickContainerAsync(pod, includeInit: false);
        var title = $"{pod.Name}{(container is null ? "" : "/" + container)}";
        var vm = new TerminalViewModel(this, title, ct => Session.Actions.StartExecAsync(pod.Namespace!, pod.Name, container, null, ct));
        Dock.Add(vm);
        await vm.StartAsync();
    }

    private async Task ForwardPortAsync(KubeObject pod)
    {
        var ports = new PodViewHelper(pod).Ports();
        string? choice = ports.Count == 1 ? ports[0].ToString() : await Dialogs.PromptAsync("Forward port", "Remote pod port:", ports.FirstOrDefault().ToString());
        if (choice is null || !int.TryParse(choice, out var port)) return;
        await StartForwardAsync(new ForwardTarget(ForwardTargetKind.Pod, pod.Namespace!, pod.Name, port));
    }

    private async Task ForwardServicePortAsync(KubeObject svc)
    {
        var ports = (svc.At("spec", "ports") as System.Text.Json.Nodes.JsonArray ?? []).Select(p => p?["port"]?.ToString() ?? "").Where(p => p.Length > 0).ToList();
        string? choice = ports.Count == 1 ? ports[0] : await Dialogs.ChooseAsync("Forward service port", ports);
        if (choice is null || !int.TryParse(choice, out var port)) return;
        await StartForwardAsync(new ForwardTarget(ForwardTargetKind.Service, svc.Namespace!, svc.Name, port));
    }

    public async Task StartForwardAsync(ForwardTarget target)
    {
        try
        {
            var pf = await Session.Actions.StartPortForwardAsync(target, 0, false, Session.SessionToken);
            EnsurePortForwardsDock();
            Main.SetStatus($"Forwarding {pf.Url} → {target.Name}:{target.RemotePort}");
        }
        catch (Exception ex) { await Dialogs.ShowErrorAsync("Port forward failed", ex.Message); }
    }

    private void EnsurePortForwardsDock() => EnsurePortForwards();

    private async Task RestoreKeptForwardsAsync()
    {
        foreach (var k in Session.Metadata.KeptForwards.ToList())
        {
            try
            {
                var kind = Enum.TryParse<ForwardTargetKind>(k.Kind, out var kk) ? kk : ForwardTargetKind.Pod;
                var pf = await Session.Actions.StartPortForwardAsync(new ForwardTarget(kind, k.Namespace, k.Name, k.RemotePort), k.LocalPort, true, Session.SessionToken);
                pf.Keep = true;
            }
            catch (Exception ex) { AppLog.Warn($"Cannot restore kept port-forward {k.Name}: {ex.Message}"); }
        }
        if (Session.PortForwards?.Forwards.Count > 0) Ui.Post(() => EnsurePortForwardsDock());
    }

    // ---------- lifecycle ----------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _ageTimer.Dispose();
        if (_sampler is not null) await _sampler.DisposeAsync();
        await Dock.CloseAllAsync();
        if (CurrentContent is IClosable c) await c.DisposeAsync();
        CurrentContent = null;
        await Namespaces.DisposeAsync();
    }
}

internal sealed class PodViewHelper(KubeObject pod)
{
    public IReadOnlyList<string> ContainerNames(bool includeInit)
    {
        var pv = new Core.Views.PodView(pod);
        return pv.Containers.Where(c => includeInit || !c.IsInit).Select(c => c.Name).ToList();
    }

    public IReadOnlyList<int> Ports() => new Core.Views.PodView(pod).Containers.SelectMany(c => c.Ports).Distinct().ToList() is { Count: > 0 } l ? l : [80];
}
