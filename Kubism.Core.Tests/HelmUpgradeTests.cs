using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Core.Events;
using Kubism.Core.Helm;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class HelmUpgradeTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>The shape of the <c>chart</c> object helm stores in a release.</summary>
    private static JsonObject Chart(string template = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: {{ .Release.Name }}-cfg\ndata:\n  greeting: {{ .Values.greeting | quote }}\n", bool dependencies = false) => new()
    {
        ["metadata"] = new JsonObject
        {
            ["name"] = "hello", ["version"] = "1.2.3", ["appVersion"] = "9", ["apiVersion"] = "v2",
            ["dependencies"] = dependencies ? new JsonArray(new JsonObject { ["name"] = "redis", ["version"] = "1.0.0", ["repository"] = "https://x" }) : null,
        },
        ["values"] = new JsonObject { ["greeting"] = "hello", ["replicas"] = 1 },
        ["templates"] = new JsonArray(new JsonObject { ["name"] = "templates/cm.yaml", ["data"] = B64(template) }),
        ["files"] = new JsonArray(new JsonObject { ["name"] = "README.md", ["data"] = B64("# hi") }),
        ["schema"] = B64("{\"type\":\"object\"}"),
    };

    [Fact]
    public void Exporter_writes_a_chart_folder_from_the_release()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kubism-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            HelmChartExporter.Write(Chart(), dir);
            Assert.Contains("name: hello", File.ReadAllText(Path.Combine(dir, "Chart.yaml")));
            Assert.Contains("greeting: hello", File.ReadAllText(Path.Combine(dir, "values.yaml")));
            Assert.Contains("{{ .Release.Name }}-cfg", File.ReadAllText(Path.Combine(dir, "templates", "cm.yaml")));
            Assert.Equal("# hi", File.ReadAllText(Path.Combine(dir, "README.md")));
            Assert.Equal("{\"type\":\"object\"}", File.ReadAllText(Path.Combine(dir, "values.schema.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("../evil.yaml")]
    [InlineData("templates/../../evil.yaml")]
    [InlineData("/etc/evil.yaml")]
    public void Exporter_refuses_file_names_that_leave_the_chart_folder(string name)
    {
        var chart = Chart();
        ((JsonArray)chart["files"]!).Add(new JsonObject { ["name"] = name, ["data"] = B64("x") });
        var dir = Path.Combine(Path.GetTempPath(), "kubism-test-" + Guid.NewGuid().ToString("N")[..8]);
        try { Assert.Throws<InvalidDataException>(() => HelmChartExporter.Write(chart, dir)); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_chart_with_subcharts_or_without_templates_cannot_be_rebuilt()
    {
        Assert.Null(HelmUpgrade.CannotRebuildReason(Chart()));
        Assert.Contains("dependencies", HelmUpgrade.CannotRebuildReason(Chart(dependencies: true)));
        Assert.Contains("templates", HelmUpgrade.CannotRebuildReason(new JsonObject { ["metadata"] = new JsonObject { ["name"] = "x" } }));
        Assert.NotNull(HelmUpgrade.CannotRebuildReason(null));
    }

    [Fact]
    public void Command_lines_for_upgrade_and_preview()
    {
        var reuse = new HelmUpgradeRequest("web", "nginx", "", "9.9.9", "a: 1", Wait: false, Atomic: false, Chart());
        Assert.Equal(["upgrade", "nginx", "/c", "-n", "web", "-f", "/v", "--reset-values"], HelmUpgrade.BuildArgs(reuse, "/c", "/v", template: false));   // no --version for the rebuilt chart
        var repo = new HelmUpgradeRequest("web", "nginx", "bitnami/nginx", "15.1.0", "a: 1", Wait: true, Atomic: false);
        Assert.Equal(["upgrade", "nginx", "bitnami/nginx", "-n", "web", "-f", "/v", "--reset-values", "--wait", "--version", "15.1.0"], HelmUpgrade.BuildArgs(repo, "bitnami/nginx", "/v", template: false));
        Assert.Contains("--atomic", HelmUpgrade.BuildArgs(repo with { Atomic = true }, "bitnami/nginx", "/v", template: false));
        Assert.DoesNotContain("--wait", HelmUpgrade.BuildArgs(repo with { Atomic = true }, "bitnami/nginx", "/v", template: false));
        Assert.Equal(["template", "nginx", "bitnami/nginx", "-n", "web", "-f", "/v", "--no-hooks", "--is-upgrade", "--version", "15.1.0"], HelmUpgrade.BuildArgs(repo, "bitnami/nginx", "/v", template: true));
    }

    private sealed class FakeHelm(string output = "ok", bool available = true) : IHelmRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<string> ChartFiles { get; } = [];
        public string? Values { get; private set; }
        public bool IsAvailable => available;
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add([.. args]);
            // The scratch folder exists while helm runs.
            Values = File.ReadAllText(args[args.ToList().IndexOf("-f") + 1]);
            if (Directory.Exists(args[2])) ChartFiles.AddRange(Directory.GetFiles(args[2], "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(args[2], f).Replace('\\', '/')));
            return Task.FromResult(output);
        }
    }

    [Fact]
    public async Task Preview_diffs_the_new_render_against_the_installed_manifest()
    {
        var current = "---\n# Source: hello/templates/cm.yaml\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: r-cfg\ndata:\n  greeting: \"hello\"\n";
        var helm = new FakeHelm("---\n# Source: hello/templates/cm.yaml\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: r-cfg\ndata:\n  greeting: \"bonjour\"\n");
        var req = new HelmUpgradeRequest("d", "r", "", null, "greeting: bonjour\n", false, false, Chart());
        var (diff, changed, _) = await HelmUpgrade.PreviewAsync(helm, req, current, default);
        Assert.True(changed);
        Assert.Contains("-   greeting: \"hello\"", diff); Assert.Contains("+   greeting: \"bonjour\"", diff);
        Assert.Equal("template", helm.Calls[0][0]);
        Assert.Equal("greeting: bonjour\n", helm.Values);
        Assert.Contains("Chart.yaml", helm.ChartFiles); Assert.Contains("templates/cm.yaml", helm.ChartFiles);

        var same = await HelmUpgrade.PreviewAsync(new FakeHelm(current + "\n\n"), req, current, default);
        Assert.False(same.Changed);   // blank lines and trailing spaces are not changes
    }

    [Fact]
    public async Task Preview_refuses_to_rebuild_a_chart_with_subcharts()
    {
        var req = new HelmUpgradeRequest("d", "r", "", null, "", false, false, Chart(dependencies: true));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HelmUpgrade.PreviewAsync(new FakeHelm(), req, "", default));
        Assert.Contains("dependencies", ex.Message);
    }

    private static ClusterActions Make(FakeHelm helm, SafetyMode mode)
    {
        var t = new FakeKubeTransport();
        var a = new ClusterActions("c", new ApiDiscovery(t), t, new ClusterEventsService(), new ActivityLog(), () => mode) { Helm = helm };
        return a;
    }

    [Fact]
    public async Task Upgrade_runs_helm_through_cluster_actions_with_the_safety_rules()
    {
        var helm = new FakeHelm();
        var req = new HelmUpgradeRequest("web", "nginx", "bitnami/nginx", "15.1.0", "replicaCount: 3\n", false, true);
        await Make(helm, SafetyMode.Normal).UpgradeHelmReleaseAsync(req, default);
        Assert.Equal(["upgrade", "nginx", "bitnami/nginx", "-n", "web", "-f"], helm.Calls[0][..6]);
        Assert.Contains("--atomic", helm.Calls[0]);
        Assert.Equal("replicaCount: 3\n", helm.Values);

        var prod = Make(helm, SafetyMode.Production);
        await Assert.ThrowsAsync<SafetyException>(() => prod.UpgradeHelmReleaseAsync(req, default));
        await prod.UpgradeHelmReleaseAsync(req, default, confirmation: "nginx");
        await Assert.ThrowsAsync<SafetyException>(() => Make(helm, SafetyMode.ReadOnly).UpgradeHelmReleaseAsync(req, default));
        Assert.Equal(2, helm.Calls.Count);
        Assert.Contains("HelmUpgrade", ClusterActions.Actions);
        var noHelm = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(new FakeHelm(available: false), SafetyMode.Normal).UpgradeHelmReleaseAsync(req, default));
        Assert.Contains("helm", noHelm.Message);
    }

    // ---------- against the real helm binary, when there is one ----------

    private sealed class ProcessHelm : IHelmRunner
    {
        public bool IsAvailable => Which() is not null;
        public static string? Which() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "helm.exe" : "helm")).FirstOrDefault(File.Exists);
        public async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(Which()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException($"helm failed: {await e}");
            return await o;
        }
    }

    [Fact]
    public async Task Rebuilt_chart_renders_with_the_real_helm_and_new_values_change_only_what_they_should()
    {
        Assert.SkipUnless(new ProcessHelm().IsAvailable, "the helm binary is not installed");
        var helm = new ProcessHelm();
        // The installed manifest is what the same chart renders with the values it was installed with.
        string current;
        using (var ws = new HelmUpgrade.Workspace(new HelmUpgradeRequest("d", "r", "", null, "{}\n", false, false, Chart())))
            current = await helm.RunAsync(["template", "r", ws.ChartRef, "-n", "d", "--no-hooks", "--is-upgrade"], default);
        Assert.Contains("greeting: \"hello\"", current);

        var unchanged = await HelmUpgrade.PreviewAsync(helm, new HelmUpgradeRequest("d", "r", "", null, "{}\n", false, false, Chart()), current, default);
        Assert.False(unchanged.Changed);
        var changed = await HelmUpgrade.PreviewAsync(helm, new HelmUpgradeRequest("d", "r", "", null, "greeting: bonjour\n", false, false, Chart()), current, default);
        Assert.True(changed.Changed);
        Assert.Contains("+   greeting: \"bonjour\"", changed.Diff);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HelmUpgrade.PreviewAsync(helm, new HelmUpgradeRequest("d", "r", "", null, "greeting: [unclosed\n", false, false, Chart()), current, default));   // helm's own error is surfaced
    }
}
