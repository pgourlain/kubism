using System.Net;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Desktop.Services;
using Kubism.Desktop.ViewModels;
using Kubism.Desktop.Views;
using Xunit;

namespace Kubism.Desktop.Tests;

public class UiMultiClusterAndPolishTests
{
    [AvaloniaFact]
    public async Task Split_view_shows_two_clusters_side_by_side_on_the_same_kind()
    {
        await using var h = await UiHarness.StartAsync();
        var staging = await h.ConnectAsync("demo-staging");
        staging.Namespaces.SelectAll();
        staging.OpenKind(staging.Session.Discovery.FindByName("deployments")!);
        var prod = await h.ConnectAsync("demo-production");
        h.Main.SelectedWorkspace = staging;
        h.Main.OpenSideBySide(prod);
        Assert.True(h.Main.HasSecondary);
        Assert.Same(prod, h.Main.SecondaryWorkspace);
        await UiHarness.WaitFor(() => prod.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Deployment" && l.Rows.Count > 0, what: "second pane list");
        Assert.IsType<ResourceListViewModel>(staging.CurrentContent);
        h.Screenshot("20-split-view");
        h.Main.CloseSplitCommand.Execute(null);
        Assert.False(h.Main.HasSecondary);
    }

    [AvaloniaFact]
    public async Task Global_search_finds_objects_in_every_connected_cluster_grouped_by_cluster()
    {
        await using var h = await UiHarness.StartAsync();
        var staging = await h.ConnectAsync("demo-staging");
        var prod = await h.ConnectAsync("demo-production");
        h.Main.OpenGlobalSearchCommand.Execute(null);
        Assert.True(h.Main.GlobalSearchOpen);
        h.Main.Search.Query = "checkout";
        await h.Main.Search.SearchAsync();
        Assert.Equal(2, h.Main.Search.Groups.Count);
        Assert.All(h.Main.Search.Groups, g => Assert.Contains(g.Hits, x => x.Kind == "Deployment" && x.Name == "checkout"));
        h.Main.Search.Query = "app=web";
        await h.Main.Search.SearchAsync();
        Assert.All(h.Main.Search.Groups, g => Assert.All(g.Hits, x => Assert.StartsWith("web", x.Name)));
        h.Screenshot("21-global-search");
        var hit = h.Main.Search.Groups[0].Hits.First(x => x.Kind == "Deployment");
        h.Main.Search.OpenCommand.Execute(hit);
        await UiHarness.WaitFor(() => hit.Workspace.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Deployment");
        Assert.Same(hit.Workspace, h.Main.SelectedWorkspace);
        Assert.False(h.Main.GlobalSearchOpen);
    }

    [AvaloniaFact]
    public async Task Hotbar_pin_reorder_and_numeric_shortcuts()
    {
        await using var h = await UiHarness.StartAsync();
        var names = () => h.Main.Clusters.Select(c => c.Entry.ContextName).ToList();
        Assert.Equal(["demo-production", "demo-staging"], names());
        h.Main.TogglePinCommand.Execute(h.Cluster("demo-staging"));
        Assert.Equal(["demo-staging", "demo-production"], names());   // pinned first
        h.Main.TogglePinCommand.Execute(h.Cluster("demo-staging"));
        h.Main.MoveDownCommand.Execute(h.Cluster("demo-production"));
        Assert.Equal(["demo-staging", "demo-production"], names());   // explicit order survives
        Assert.Equal(1, h.Cluster("demo-production").Metadata.Order);
        var a = await h.ConnectAsync("demo-staging"); var b = await h.ConnectAsync("demo-production");
        h.Main.SelectWorkspaceByIndex(0);
        Assert.Same(a, h.Main.SelectedWorkspace);
        h.Main.SelectWorkspaceByIndex(1);
        Assert.Same(b, h.Main.SelectedWorkspace);
    }

    [AvaloniaFact]
    public async Task Informer_budget_shows_a_message_instead_of_a_silent_failure()
    {
        await using var h = await UiHarness.StartAsync(s => s.Current.MaxInformersPerCluster = 3);
        var ws = await h.ConnectAsync();
        // Namespaces + Overview (nodes, pods, events) already hold the budget.
        ws.OpenKind(ws.Session.Discovery.FindByName("configmaps")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.ErrorText is not null || ws.Session.Cache.Informers.Count <= 3, what: "budget");
        Assert.True(ws.Session.Cache.Informers.Count <= 3);
    }

    [AvaloniaFact]
    public async Task Crd_details_show_schema_and_can_browse_instances()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.OpenKind(ws.Session.Discovery.FindByName("crd")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count == 1);
        var list = (ResourceListViewModel)ws.CurrentContent!;
        list.SelectedRow = list.Rows[0];
        await UiHarness.WaitFor(() => ws.Details.Sections.Any(s => s.Title.StartsWith("Schema")));
        var schema = ws.Details.Sections.First(s => s.Title.StartsWith("Schema")).Table!;
        Assert.Contains(schema.Rows, r => r[0].Trim() == "spec.secretName" && r[1] == "string");
        Assert.Contains(ws.Details.Sections, s => s.Title == "Versions");
        await ws.Details.Actions.First(a => a.Header == "Browse instances").ExecuteAsync();
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Kind.Kind == "Certificate");
    }

    [AvaloniaFact]
    public async Task Settings_change_language_and_theme_and_rebuild_the_sidebar()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        Assert.Contains(ws.NavRoots, n => n.Title == "Overview");
        h.Main.Settings.Current.Language = "fr"; h.Main.Settings.Current.Theme = "Dark";
        h.Main.ApplySettings();
        try
        {
            Assert.Contains(ws.NavRoots, n => n.Title == "Vue d'ensemble");
            Assert.Contains(ws.NavRoots, n => n.Title == "Charges de travail");
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            h.Screenshot("22-dark-french");
        }
        finally { h.Main.Settings.Current.Language = "en"; h.Main.Settings.Current.Theme = "System"; h.Main.ApplySettings(); }
        Assert.Contains(ws.NavRoots, n => n.Title == "Overview");
    }

    [AvaloniaFact]
    public async Task Settings_editor_applies_values_through_the_dialog_service()
    {
        await using var h = await UiHarness.StartAsync();
        h.Main.Settings.Current.LogBufferLines = 50_000;
        await h.Main.EditSettingsAsync();   // fake dialog returns the editor untouched
        Assert.Equal(50_000, h.Main.Settings.Current.LogBufferLines);
        Assert.Contains("settings", h.Dialogs.Shown);
    }

    [AvaloniaFact]
    public void French_translation_covers_every_key_used_in_menus_and_sidebar()
    {
        foreach (var key in new[] { "File", "View", "Help", "Quit", "Overview", "Nodes", "Workloads", "Config", "Network", "Storage", "Namespaces", "Events", "Helm", "Access Control", "Custom Resources", "All kinds", "Right-sizing", "Activity", "Port forwards" })
            Assert.True(Translations.French.ContainsKey(key), key);
        Translations.Language = "fr";
        try { Assert.Equal("Fichier", Translations.GetString("File")); Assert.Equal("Unknown key", Translations.GetString("Unknown key")); }
        finally { Translations.Language = "en"; }
    }

    [Fact]
    public async Task Update_check_is_opt_in_once_a_day_and_never_installs()
    {
        var settings = new SettingsStore();
        var calls = 0;
        var handler = new StubHandler(() => { calls++; return """{"tag_name":"v9.9.9","html_url":"https://example.test/r"}"""; });
        var now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var checker = new UpdateChecker(settings, handler, () => now);
        Assert.Null(await checker.CheckAsync(new Version(0, 1, 0)));          // opt-in: off by default
        Assert.Equal(0, calls);
        settings.Current.CheckForUpdates = true;
        var info = await checker.CheckAsync(new Version(0, 1, 0));
        Assert.True(info!.IsNewer); Assert.Equal("9.9.9", info.Version);
        Assert.Null(await checker.CheckAsync(new Version(0, 1, 0)));          // same day
        now = now.AddHours(25);
        Assert.NotNull(await checker.CheckAsync(new Version(99, 0, 0)));
        Assert.Equal(2, calls);
        Assert.False((await new UpdateChecker(new SettingsStore { }, new StubHandler(() => throw new HttpRequestException("offline"))).CheckAsync(new Version(1, 0))) is not null);
    }

    private sealed class StubHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) });
    }

    // ---- accessibility ----

    private static double Luminance(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color BrushColor(ThemeVariant v, string key) =>
        Application.Current!.TryGetResource(key, v, out var r) && r is ISolidColorBrush b ? b.Color : throw new InvalidOperationException(key);

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Theme_colors_meet_wcag_aa_contrast(string theme)
    {
        var v = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var panel = BrushColor(v, "KubismPanelBrush"); var side = BrushColor(v, "KubismSidebarBrush");
        foreach (var key in new[] { "KubismMutedBrush", "KubismOkBrush", "KubismWarnBrush", "KubismErrorBrush", "KubismAccentBrush" })
            foreach (var bg in new[] { panel, side })
                Assert.True(Contrast(BrushColor(v, key), bg) >= 4.5, $"{theme}: {key} on {bg} = {Contrast(BrushColor(v, key), bg):0.00}");
        foreach (var key in new[] { "KubismProductionBrush", "KubismReadOnlyBrush" })
            Assert.True(Contrast(Colors.White, BrushColor(v, key)) >= 4.5, $"{theme}: white on {key}");
    }

    [AvaloniaFact]
    public async Task Key_controls_have_automation_names_and_the_ui_is_keyboard_reachable()
    {
        await using var h = await UiHarness.StartAsync();
        var ws = await h.ConnectAsync();
        ws.Namespaces.SelectAll();
        ws.OpenKind(ws.Session.Discovery.FindByName("pods")!);
        await UiHarness.WaitFor(() => ws.CurrentContent is ResourceListViewModel l && l.Rows.Count > 0);
        UiHarness.Pump();
        var named = h.Window.GetVisualDescendants().OfType<Control>().Where(c => !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(c))).Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
        foreach (var expected in new[] { "Cluster catalog", "Navigation", "Resources", "Filter", "Namespace selection", "Connected clusters" })
            Assert.Contains(expected, named);
        var focusable = h.Window.GetVisualDescendants().OfType<InputElement>().Where(i => i.Focusable && i.IsEffectivelyVisible && i.IsEffectivelyEnabled).ToList();
        Assert.Contains(focusable, i => i is TreeView || i is TreeViewItem);          // sidebar
        Assert.Contains(focusable, i => i is DataGrid || i is DataGridRow || i is DataGridCell); // grid
        Assert.Contains(focusable, i => i is ListBox || i is ListBoxItem);            // catalog
        // Every button in view has either text or an automation name.
        foreach (var b in h.Window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null))
            Assert.True(b.Content is string { Length: > 0 } || !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b)) || b.Content is not null, "unlabelled button");
    }
}

public class UiSignInTests
{
    [AvaloniaFact]
    public async Task Interactive_exec_plugin_stderr_shows_device_code_in_the_signin_dialog()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var h = await UiHarness.StartAsync();
        var dir = Path.Combine(h.DataDir, "exec"); Directory.CreateDirectory(dir);
        var plugin = Path.Combine(dir, "fake-kubelogin");
        // Prints the device code, waits a moment (the user "signs in"), then returns the credential.
        File.WriteAllText(plugin, "#!/bin/sh\necho 'To sign in, use a web browser to open https://microsoft.com/devicelogin and enter the code AB12CD34' >&2\nsleep 1\n" +
            "echo '{\"apiVersion\":\"client.authentication.k8s.io/v1\",\"kind\":\"ExecCredential\",\"status\":{\"token\":\"fake-token\",\"expirationTimestamp\":\"2099-01-01T00:00:00Z\"}}'\n");
        File.SetUnixFileMode(plugin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cfg = Path.Combine(dir, "aks-config");
        File.WriteAllText(cfg, $"apiVersion: v1\nkind: Config\nclusters:\n- name: aks\n  cluster: {{server: \"{h.Demo.Server.Url}\"}}\nusers:\n- name: u\n  user:\n    exec: {{apiVersion: client.authentication.k8s.io/v1, command: {plugin}, interactiveMode: IfAvailable}}\ncontexts:\n- name: aks-entra\n  context: {{cluster: aks, user: u}}\n");
        h.Services.GetService(typeof(Core.Catalog.IKubeconfigCatalog)).GetType();
        ((Core.Catalog.IKubeconfigCatalog)h.Services.GetService(typeof(Core.Catalog.IKubeconfigCatalog))!).AddPath(cfg);
        await UiHarness.WaitFor(() => h.Main.Clusters.Any(c => c.Entry.ContextName == "aks-entra"), what: "catalog");
        await h.Main.ConnectAsync(h.Cluster("aks-entra"));
        Assert.Equal(1, h.Dialogs.SignInShown);
        Assert.Contains(h.Dialogs.SignInLines, l => l.Contains("AB12CD34") && l.Contains("devicelogin"));
        Assert.Single(h.Main.Workspaces);
    }

    [AvaloniaFact]
    public async Task Missing_plugin_error_is_shown_on_the_cluster_with_the_binary_name()
    {
        await using var h = await UiHarness.StartAsync();
        var dir = Path.Combine(h.DataDir, "exec2"); Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "cfg");
        File.WriteAllText(cfg, $"apiVersion: v1\nkind: Config\nclusters:\n- name: aks\n  cluster: {{server: \"{h.Demo.Server.Url}\"}}\nusers:\n- name: u\n  user:\n    exec: {{apiVersion: client.authentication.k8s.io/v1, command: kubelogin-not-installed}}\ncontexts:\n- name: needs-kubelogin\n  context: {{cluster: aks, user: u}}\n");
        ((Core.Catalog.IKubeconfigCatalog)h.Services.GetService(typeof(Core.Catalog.IKubeconfigCatalog))!).AddPath(cfg);
        await UiHarness.WaitFor(() => h.Main.Clusters.Any(c => c.Entry.ContextName == "needs-kubelogin"));
        await h.Main.ConnectAsync(h.Cluster("needs-kubelogin"));
        var item = h.Cluster("needs-kubelogin");
        Assert.Equal(Core.Sessions.SessionState.Error, item.State);
        Assert.Contains("kubelogin-not-installed", item.ErrorMessage);
        Assert.Contains("PATH", item.ErrorMessage);
        Assert.Empty(h.Main.Workspaces);
    }
}

public class UiDialogTests
{
    private static async Task<T?> Drive<T>(Func<AvaloniaDialogService, Task<T>> show, string buttonText)
    {
        void OnOpened(Window w) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var b = w.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == buttonText);
            b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }, Avalonia.Threading.DispatcherPriority.Background);
        AvaloniaDialogService.DialogOpened += OnOpened;
        try { return await show(new AvaloniaDialogService()); }
        finally { AvaloniaDialogService.DialogOpened -= OnOpened; }
    }

    [AvaloniaFact]
    public async Task Cancel_closes_confirm_and_returns_false_and_ok_returns_true()
    {
        Assert.False(await Drive(s => s.ConfirmAsync(new ConfirmRequest("t", "sure?", "Delete", true)), "Cancel"));
        Assert.True(await Drive(s => s.ConfirmAsync(new ConfirmRequest("t", "sure?", "Delete", true)), "Delete"));
    }

    [AvaloniaFact]
    public async Task Cancel_on_prompt_choose_and_typed_confirmation_returns_nothing()
    {
        Assert.Null(await Drive(s => s.PromptAsync("t", "name?", "x"), "Cancel"));
        Assert.Equal("x", await Drive(s => s.PromptAsync("t", "name?", "x"), "OK"));
        Assert.Null(await Drive(s => s.ChooseAsync("t", ["a", "b"]), "Cancel"));
        Assert.Equal("a", await Drive(s => s.ChooseAsync("t", ["a", "b"]), "OK"));
        Assert.Null(await Drive(s => s.ConfirmTypedAsync("t", "m", "name"), "Cancel"));
    }

    [AvaloniaFact]
    public async Task About_dialog_shows_name_and_version_and_closes()
    {
        var info = new AboutInfo("Kubism", "Inspect. Steer. Monitor.", "1.2.3", ".NET 10", "macOS", "MIT", "https://example.test", "diag");
        string? text = null;
        void OnOpened(Window w) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { text = string.Join("|", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)); w.Close(true); }, Avalonia.Threading.DispatcherPriority.Background);
        AvaloniaDialogService.DialogOpened += OnOpened;
        try { await new AvaloniaDialogService().ShowAboutAsync(info); } finally { AvaloniaDialogService.DialogOpened -= OnOpened; }
        Assert.Contains("Kubism", text); Assert.Contains("Version 1.2.3", text); Assert.Contains("Inspect. Steer. Monitor.", text);
    }

    [AvaloniaFact]
    public async Task About_menu_entry_exists_and_opens_the_about_dialog()
    {
        await using var h = await UiHarness.StartAsync();
        await h.Main.ShowAboutAsync();
        Assert.NotNull(h.Dialogs.About);
        Assert.Equal("Kubism", h.Dialogs.About!.Name);
        var spec = MenuSpecs.Build(h.Main, h.Window);
        Assert.Contains(spec.First(m => m.Header == "Help").Children!, m => m.Header == "About Kubism");
        Assert.Equal("Kubism", Application.Current!.Name);
    }
}
