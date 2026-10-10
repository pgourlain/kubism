using System.Text.Json;
using System.Text.Json.Nodes;
using Kubism.Core.Catalog;
using Kubism.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Kubism.Mcp.Tests;

/// <summary>Drives the real `Kubism --mcp` over stdio against the fake API server.</summary>
public sealed class McpFixture : IAsyncDisposable
{
    public FakeApiServer Server { get; private set; } = null!;
    public FakePrometheus Prometheus { get; private set; } = null!;
    public string Dir { get; private set; } = "";
    public string Kubeconfig { get; private set; } = "";
    public McpClient Client { get; private set; } = null!;
    public string[] ToolNames { get; private set; } = [];

    public static string FindKubismDll()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Kubism.slnx"))) d = d.Parent;
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(d!.FullName, "Kubism.Desktop", "bin", cfg, "net10.0", "Kubism.dll");
    }

    public static async Task<McpFixture> StartAsync(string[] extraArgs, Action<ISettingsStore, string>? configure = null)
    {
        var f = new McpFixture
        {
            Server = await FakeApiServer.StartAsync(), Prometheus = await FakePrometheus.StartAsync(),
            Dir = Path.Combine(Path.GetTempPath(), "kubism-mcp-" + Guid.NewGuid().ToString("N")[..8]),
        };
        Directory.CreateDirectory(f.Dir);
        f.Kubeconfig = f.Server.WriteKubeconfig(f.Dir, "fake");
        var data = Path.Combine(f.Dir, "data");
        Directory.CreateDirectory(data);
        foreach (var p in f.Server.List(Core.Api.Gvr.Pods, "default")) f.Prometheus.Steady("default", p["metadata"]!["name"]!.GetValue<string>(), "main", 0.02, 40 * 1024 * 1024);
        var settings = new SettingsStore(Path.Combine(data, "settings.json"));
        var id = KubeconfigCatalog.ParseFile(f.Kubeconfig)[0].Id;
        var meta = settings.GetMetadata(id); meta.PrometheusUrl = f.Prometheus.Url; settings.SetMetadata(id, meta);
        configure?.Invoke(settings, id);
        // KUBISM_MCP_EXE: drive a published (Native AOT) executable instead of `dotnet Kubism.dll`.
        var exe = Environment.GetEnvironmentVariable("KUBISM_MCP_EXE");
        var args = new List<string>();
        if (exe is null) args.Add(FindKubismDll());
        args.AddRange(["--mcp", "--data", data]);
        args.AddRange(extraArgs);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "kubism", Command = exe ?? "dotnet", Arguments = args,
            EnvironmentVariables = new Dictionary<string, string?> { ["KUBECONFIG"] = f.Kubeconfig, ["HOME"] = f.Dir, ["USERPROFILE"] = f.Dir, ["KUBISM_DATA"] = data },
        });
        f.Client = await McpClient.CreateAsync(transport);
        f.ToolNames = (await f.Client.ListToolsAsync()).Select(t => t.Name).ToArray();
        return f;
    }

    public async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var r = await Client.CallToolAsync(tool, args!);
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (r.IsError == true) throw new McpException(text);
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); } catch { }
        await Prometheus.DisposeAsync();
        await Server.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class McpReadToolsTests
{
    private static readonly string[] SecretMarkers = ["supersecret", "c3VwZXJzZWNyZXQ=", "YWRtaW4="];

    [Fact]
    public async Task Server_lists_read_tools_and_hides_write_tools_by_default()
    {
        await using var f = await McpFixture.StartAsync([]);
        foreach (var t in new[] { "list_clusters", "list_kinds", "list_resources", "get_resource", "get_events", "get_logs", "top_pods", "top_nodes", "rightsizing_report", "describe", "rbac_who_can", "rbac_rights_of", "network_exposure" })
            Assert.Contains(t, f.ToolNames);
        foreach (var t in new[] { "apply", "delete", "scale", "rollout_restart" })
            Assert.DoesNotContain(t, f.ToolNames);
    }

    [Fact]
    public async Task Clusters_kinds_and_resources()
    {
        await using var f = await McpFixture.StartAsync([]);
        var clusters = JsonNode.Parse(await f.CallAsync("list_clusters", []))!.AsArray();
        Assert.Equal("fake", clusters[0]!["context"]!.GetValue<string>());
        Assert.Equal("Normal", clusters[0]!["safetyMode"]!.GetValue<string>());
        var kinds = JsonNode.Parse(await f.CallAsync("list_kinds", new() { ["cluster"] = "fake", ["filter"] = "cert" }))!.AsArray();
        Assert.Contains(kinds, k => k!["kind"]!.GetValue<string>() == "Certificate");
        var pods = JsonNode.Parse(await f.CallAsync("list_resources", new() { ["cluster"] = "fake", ["kind"] = "po", ["namespace"] = "default", ["labelSelector"] = "app=web" }))!;
        Assert.Equal(3, pods["count"]!.GetValue<int>());
        Assert.Equal("Running", pods["rows"]![0]!["Status"]!.GetValue<string>());
        var one = await f.CallAsync("get_resource", new() { ["cluster"] = "fake", ["kind"] = "deploy", ["name"] = "web", ["namespace"] = "default" });
        Assert.Contains("kind: Deployment", one);
        Assert.DoesNotContain("managedFields", one);
        var ev = JsonNode.Parse(await f.CallAsync("get_events", new() { ["cluster"] = "fake", ["namespace"] = "default" }))!.AsArray();
        Assert.Contains(ev, e => e!["reason"]!.GetValue<string>() == "BackOff");
    }

    [Fact]
    public async Task Logs_are_bounded_and_describe_includes_events_and_owners()
    {
        await using var f = await McpFixture.StartAsync([]);
        var pod = f.Server.List(Core.Api.Gvr.Pods, "default").First(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("web-"))["metadata"]!["name"]!.GetValue<string>();
        f.Server.SetLogs("default", pod, Enumerable.Range(0, 3000).Select(i => $"{DateTimeOffset.UtcNow:O} line {i} {new string('x', 100)}"));
        var logs = await f.CallAsync("get_logs", new() { ["cluster"] = "fake", ["namespace"] = "default", ["pod"] = pod, ["tailLines"] = 5000 });
        Assert.True(logs.Length <= 70 * 1024);
        Assert.Contains("(truncated)", logs);
        var d = await f.CallAsync("describe", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = "crashy-0", ["namespace"] = "default" });
        Assert.Contains("CrashLoopBackOff", d);
        Assert.Contains("BackOff", d);
        Assert.Contains("## Owners", d);
        var dw = await f.CallAsync("describe", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = pod, ["namespace"] = "default" });
        Assert.Contains("- ReplicaSet/", dw); Assert.Contains("- Deployment/web", dw);
    }

    [Fact]
    public async Task Top_pods_nodes_and_rightsizing_report()
    {
        await using var f = await McpFixture.StartAsync([]);
        f.Server.PodMetrics["default"] = new() { ["web-1"] = ("250m", "100Mi"), ["api-1"] = ("50m", "50Mi") };
        f.Server.NodeMetrics["node-1"] = ("1", "2Gi");
        var top = JsonNode.Parse(await f.CallAsync("top_pods", new() { ["cluster"] = "fake" }))!.AsArray();
        Assert.Equal("web-1", top[0]!["pod"]!.GetValue<string>());
        Assert.Equal("1", JsonNode.Parse(await f.CallAsync("top_nodes", new() { ["cluster"] = "fake" }))![0]!["cpu"]!.GetValue<string>());
        var rs = JsonNode.Parse(await f.CallAsync("rightsizing_report", new() { ["cluster"] = "fake", ["namespace"] = "default", ["days"] = 7 }))!;
        Assert.Equal("Prometheus", rs["source"]!.GetValue<string>());
        var web = rs["workloads"]!.AsArray().First(w => w!["name"]!.GetValue<string>() == "web")!;
        Assert.Equal("OverProvisioned", web["containers"]![0]!["verdict"]!.GetValue<string>());
        Assert.NotNull(web["containers"]![0]!["recommended"]!["cpuRequest"]);
        Assert.Contains("resources:", rs["yamlSnippet"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_tool_or_resource_can_leak_a_secret_value()
    {
        await using var f = await McpFixture.StartAsync([]);
        var outputs = new List<string>
        {
            await f.CallAsync("get_resource", new() { ["cluster"] = "fake", ["kind"] = "secret", ["name"] = "db-credentials", ["namespace"] = "default" }),
            await f.CallAsync("describe", new() { ["cluster"] = "fake", ["kind"] = "secrets", ["name"] = "db-credentials", ["namespace"] = "default" }),
            await f.CallAsync("list_resources", new() { ["cluster"] = "fake", ["kind"] = "secrets" }),
            await f.CallAsync("list_clusters", []),
            await f.CallAsync("list_kinds", new() { ["cluster"] = "fake" }),
            await f.CallAsync("get_events", new() { ["cluster"] = "fake" }),
            await f.CallAsync("rightsizing_report", new() { ["cluster"] = "fake" }),
        };
        // A pod referencing the secret through env: only the reference may appear.
        var pod = f.Server.List(Core.Api.Gvr.Pods, "default")[0];
        pod["spec"]!["containers"]![0]!["env"] = new JsonArray((JsonNode?)new JsonObject { ["name"] = "DB_PASSWORD", ["valueFrom"] = new JsonObject { ["secretKeyRef"] = new JsonObject { ["name"] = "db-credentials", ["key"] = "password" } } });
        f.Server.Upsert(pod);
        outputs.Add(await f.CallAsync("get_resource", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = pod["metadata"]!["name"]!.GetValue<string>(), ["namespace"] = "default" }));
        foreach (var o in outputs)
            foreach (var marker in SecretMarkers) Assert.DoesNotContain(marker, o);
        Assert.Contains("••••", outputs[0]);
        var res = await f.Client.ReadResourceAsync("kubism://clusters");
        Assert.Contains("fake", res.Contents.OfType<TextResourceContents>().First().Text);
        foreach (var marker in SecretMarkers) Assert.DoesNotContain(marker, res.Contents.OfType<TextResourceContents>().First().Text);
    }

    [Fact]
    public async Task Resource_template_lists_kinds_of_one_cluster()
    {
        await using var f = await McpFixture.StartAsync([]);
        var id = Uri.EscapeDataString(Core.Catalog.KubeconfigCatalog.ParseFile(f.Kubeconfig)[0].Id);
        var res = await f.Client.ReadResourceAsync($"kubism://clusters/{id}/kinds");
        Assert.Contains("Certificate", res.Contents.OfType<TextResourceContents>().First().Text);
    }

    [Fact]
    public async Task Unknown_cluster_and_kind_produce_clear_tool_errors()
    {
        await using var f = await McpFixture.StartAsync([]);
        var ex = await Assert.ThrowsAsync<McpException>(() => f.CallAsync("list_kinds", new() { ["cluster"] = "nope" }));
        Assert.Contains("Unknown cluster 'nope'", ex.Message);
        var ex2 = await Assert.ThrowsAsync<McpException>(() => f.CallAsync("get_resource", new() { ["cluster"] = "fake", ["kind"] = "widgets", ["name"] = "x" }));
        Assert.Contains("Unknown kind", ex2.Message);
    }
}

public class McpWriteToolsTests
{
    private static readonly string[] WriteArgs = ["--allow-writes"];

    [Fact]
    public async Task Write_tools_exist_only_with_allow_writes_and_go_through_cluster_actions()
    {
        await using var f = await McpFixture.StartAsync(WriteArgs);
        foreach (var t in new[] { "apply", "delete", "scale", "rollout_restart" }) Assert.Contains(t, f.ToolNames);
        await f.CallAsync("scale", new() { ["cluster"] = "fake", ["kind"] = "deploy", ["name"] = "web", ["namespace"] = "default", ["replicas"] = 5 });
        Assert.Equal(5, f.Server.Get(Core.Api.Gvr.Deployments, "default", "web")!["spec"]!["replicas"]!.GetValue<int>());
        await f.CallAsync("rollout_restart", new() { ["cluster"] = "fake", ["kind"] = "deploy", ["name"] = "web", ["namespace"] = "default" });
        Assert.NotNull(f.Server.Get(Core.Api.Gvr.Deployments, "default", "web")!["spec"]!["template"]!["metadata"]!["annotations"]);
        var applied = await f.CallAsync("apply", new() { ["cluster"] = "fake", ["namespace"] = "default", ["yaml"] = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: from-mcp\ndata:\n  a: b\n" });
        Assert.Contains("from-mcp", applied);
        await f.CallAsync("delete", new() { ["cluster"] = "fake", ["kind"] = "configmap", ["name"] = "from-mcp", ["namespace"] = "default" });
        Assert.Null(f.Server.Get(Core.Api.Gvr.ConfigMaps, "default", "from-mcp"));
    }

    [Fact]
    public async Task Production_cluster_needs_allow_production_and_the_typed_name()
    {
        void Prod(ISettingsStore s, string id) { var m = s.GetMetadata(id); m.SafetyMode = SafetyMode.Production; s.SetMetadata(id, m); }
        await using (var f = await McpFixture.StartAsync(WriteArgs, Prod))
        {
            var ex = await Assert.ThrowsAsync<McpException>(() => f.CallAsync("delete", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = "crashy-0", ["namespace"] = "default" }));
            Assert.Contains("--allow-production", ex.Message);
            Assert.NotNull(f.Server.Get(Core.Api.Gvr.Pods, "default", "crashy-0"));
        }
        await using (var f = await McpFixture.StartAsync(["--allow-writes", "--allow-production"], Prod))
        {
            var ex = await Assert.ThrowsAsync<McpException>(() => f.CallAsync("delete", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = "crashy-0", ["namespace"] = "default" }));
            Assert.Contains("crashy-0", ex.Message);
            Assert.NotNull(f.Server.Get(Core.Api.Gvr.Pods, "default", "crashy-0"));
            await f.CallAsync("delete", new() { ["cluster"] = "fake", ["kind"] = "pod", ["name"] = "crashy-0", ["namespace"] = "default", ["confirmName"] = "crashy-0" });
            Assert.Null(f.Server.Get(Core.Api.Gvr.Pods, "default", "crashy-0"));
        }
    }

    [Fact]
    public async Task Read_only_cluster_refuses_even_with_both_flags()
    {
        void Ro(ISettingsStore s, string id) { var m = s.GetMetadata(id); m.SafetyMode = SafetyMode.ReadOnly; s.SetMetadata(id, m); }
        await using var f = await McpFixture.StartAsync(["--allow-writes", "--allow-production"], Ro);
        var before = f.Server.Requests.Count(r => r.Method is "PATCH" or "DELETE" or "POST" && !r.Path.Contains("selfsubject"));
        var ex = await Assert.ThrowsAsync<McpException>(() => f.CallAsync("scale", new() { ["cluster"] = "fake", ["kind"] = "deploy", ["name"] = "web", ["namespace"] = "default", ["replicas"] = 9 }));
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, f.Server.Get(Core.Api.Gvr.Deployments, "default", "web")!["spec"]!["replicas"]!.GetValue<int>());
        Assert.Equal(before, f.Server.Requests.Count(r => r.Method is "PATCH" or "DELETE" or "POST" && !r.Path.Contains("selfsubject")));
    }
}

public class McpRbacTests
{
    [Fact]
    public async Task Rbac_who_can_returns_subjects_with_paths_and_risk_flags()
    {
        await using var f = await McpFixture.StartAsync([]);
        var r = JsonNode.Parse(await f.CallAsync("rbac_who_can", new() { ["cluster"] = "fake", ["verb"] = "get", ["resource"] = "secrets", ["namespace"] = "staging" }))!;
        Assert.True(r["complete"]!.GetValue<bool>());
        Assert.Null(r["warning"]);
        Assert.Contains("RBAC objects only", r["limits"]!.ToString());
        var subjects = r["subjects"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var devs = subjects.Single(x => x["subject"]!.ToString() == "Group devs");
        Assert.Contains(devs["paths"]!.AsArray(), p => p!["binding"]!.ToString().Contains("devs-edit") && p["role"]!.ToString().Contains("edit") && p["rule"]!.ToString().Contains("secrets"));
        Assert.Contains(devs["risks"]!.AsArray(), x => x!["code"]!.ToString() == "read-secrets");
        var everyone = subjects.Single(x => x["subject"]!.ToString() == "Group system:authenticated");
        Assert.True(everyone["everyone"]!.GetValue<bool>());
        Assert.DoesNotContain(subjects, x => x["subject"]!.ToString() == "User bob");
        Assert.Contains(r["orphanBindings"]!.AsArray(), x => x!.ToString().Contains("removed-role"));
    }

    [Fact]
    public async Task Rbac_rights_of_returns_paths_per_namespace_and_a_matrix()
    {
        await using var f = await McpFixture.StartAsync([]);
        var r = JsonNode.Parse(await f.CallAsync("rbac_rights_of", new() { ["cluster"] = "fake", ["subject"] = "Group devs", ["namespace"] = "staging" }))!;
        Assert.Equal(["production", "staging"], r["namespaces"]!.AsArray().Select(x => x!.ToString()).ToArray());
        Assert.Contains(r["paths"]!.AsArray(), p => p!["note"]?.ToString().Contains("staging only") == true);
        var exec = r["matrix"]!["resources"]!.AsArray().Single(x => x!["resource"]!.ToString() == "pods/exec")!;
        Assert.Contains("create", exec["verbs"]!.AsArray().Select(v => v!.ToString()));
        Assert.True(exec["risky"]!.GetValue<bool>());

        var ci = JsonNode.Parse(await f.CallAsync("rbac_rights_of", new() { ["cluster"] = "fake", ["subject"] = "ServiceAccount staging/ci-bot", ["namespace"] = "staging" }))!;
        var cm = ci["matrix"]!["resources"]!.AsArray().Single(x => x!["resource"]!.ToString() == "configmaps")!;
        Assert.Contains("get (named objects only)", cm["verbs"]!.AsArray().Select(v => v!.ToString()));

        var carol = JsonNode.Parse(await f.CallAsync("rbac_rights_of", new() { ["cluster"] = "fake", ["subject"] = "User carol" }))!;
        Assert.Contains(carol["notes"]!.AsArray(), n => n!.ToString().Contains("removed-role"));
        await Assert.ThrowsAsync<McpException>(() => f.CallAsync("rbac_rights_of", new() { ["cluster"] = "fake", ["subject"] = "" }));
    }

    [Fact]
    public async Task Rbac_answers_say_when_a_kind_could_not_be_read()
    {
        await using var f = await McpFixture.StartAsync([]);
        f.Server.Deny("list", "rolebindings");
        var r = JsonNode.Parse(await f.CallAsync("rbac_who_can", new() { ["cluster"] = "fake", ["verb"] = "get", ["resource"] = "pods", ["namespace"] = "staging" }))!;
        Assert.False(r["complete"]!.GetValue<bool>());
        Assert.Contains("rolebindings", r["warning"]!.ToString());
        Assert.DoesNotContain(r["subjects"]!.AsArray(), x => x!["subject"]!.ToString() == "Group devs");     // devs get pods in staging through a RoleBinding we could not read
    }
}

public class McpNetworkTests
{
    private static JsonObject Node(JsonNode r, string id) => r["nodes"]!.AsArray().Select(n => n!.AsObject()).Single(n => n["id"]!.ToString() == id);

    [Fact]
    public async Task Network_exposure_lists_chains_with_ports_exposure_and_problems()
    {
        await using var f = await McpFixture.StartAsync([]);
        var r = JsonNode.Parse(await f.CallAsync("network_exposure", new() { ["cluster"] = "fake", ["exposure"] = "outside" }))!;
        Assert.True(r["complete"]!.GetValue<bool>());
        Assert.Contains("DNS, firewalls", r["limits"]!.ToString());
        Assert.Equal("outside the cluster", Node(r, "i:shop/shop")["exposure"]!.ToString());
        Assert.Equal("outside the cluster", Node(r, "x:svc/ingress-nginx/ingress-nginx-controller")["exposure"]!.ToString());
        Assert.Contains(r["edges"]!.AsArray(), e => e!["from"]!.ToString() == "s:shop/web" && e["ports"]!.ToString() == "80 → http (8080)");
        Assert.Contains(Node(r, "i:shop/admin")["problems"]!.AsArray(), p => p!["code"]!.ToString() == "tls-secret-missing");     // looked up by GET
        Assert.Contains(Node(r, "s:shop/docs-svc")["problems"]!.AsArray(), p => p!["code"]!.ToString() == "missing-service");
        Assert.True(Node(r, "s:shop/docs-svc")["missing"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Network_exposure_filters_by_namespace_keeping_the_controller_as_context_and_by_problems()
    {
        await using var f = await McpFixture.StartAsync([]);
        var tools = JsonNode.Parse(await f.CallAsync("network_exposure", new() { ["cluster"] = "fake", ["namespace"] = "tools" }))!;
        Assert.True(Node(tools, "x:svc/ingress-nginx/ingress-nginx-controller")["context"]!.GetValue<bool>());
        Assert.DoesNotContain(tools["nodes"]!.AsArray(), n => n!["namespace"]?.ToString() == "shop");

        var problems = JsonNode.Parse(await f.CallAsync("network_exposure", new() { ["cluster"] = "fake", ["problemsOnly"] = true }))!;
        Assert.Contains(problems["nodes"]!.AsArray(), n => n!["id"]!.ToString() == "s:shop/legacy");
        Assert.DoesNotContain(problems["nodes"]!.AsArray(), n => n!["id"]!.ToString() == "s:tools/db");
        await Assert.ThrowsAsync<McpException>(() => f.CallAsync("network_exposure", new() { ["cluster"] = "fake", ["exposure"] = "sideways" }));
    }

    [Fact]
    public async Task Network_exposure_says_when_a_kind_could_not_be_read()
    {
        await using var f = await McpFixture.StartAsync([]);
        f.Server.Deny("list", "endpointslices");
        var r = JsonNode.Parse(await f.CallAsync("network_exposure", new() { ["cluster"] = "fake" }))!;
        Assert.False(r["complete"]!.GetValue<bool>());
        Assert.Contains("endpointslices", r["warning"]!.ToString());
    }
}
