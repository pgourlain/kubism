using System.Text.Json.Nodes;
using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Events;
using Kubism.Core.RightSizing;
using Kubism.Core.Sessions;
using Kubism.Kube;
using Kubism.Testing;
using Xunit;

namespace Kubism.Core.Tests;

public class RightSizingTests
{
    private const double Mi = 1024 * 1024;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static KubeObject Pod(string name, string? ownerKind = null, string? ownerName = null, string cpuReq = "1", string memReq = "1Gi", string? memLim = null, string? cpuLim = null, int restarts = 0, bool oom = false)
    {
        var res = new JsonObject { ["requests"] = new JsonObject { ["cpu"] = cpuReq, ["memory"] = memReq } };
        var lim = new JsonObject();
        if (memLim is not null) lim["memory"] = memLim;
        if (cpuLim is not null) lim["cpu"] = cpuLim;
        if (lim.Count > 0) res["limits"] = lim;
        var cs = new JsonObject { ["name"] = "main", ["restartCount"] = restarts, ["ready"] = true, ["state"] = new JsonObject { ["running"] = new JsonObject() } };
        if (oom) cs["lastState"] = new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "OOMKilled" } };
        var meta = new JsonObject { ["name"] = name, ["namespace"] = "ns" };
        if (ownerKind is not null)
            meta["ownerReferences"] = new JsonArray((JsonNode?)new JsonObject { ["kind"] = ownerKind, ["name"] = ownerName, ["apiVersion"] = "apps/v1", ["controller"] = true });
        return new KubeObject(new JsonObject
        {
            ["metadata"] = meta,
            ["spec"] = new JsonObject { ["containers"] = new JsonArray((JsonNode?)new JsonObject { ["name"] = "main", ["resources"] = res }) },
            ["status"] = new JsonObject { ["phase"] = "Running", ["qosClass"] = "Burstable", ["containerStatuses"] = new JsonArray((JsonNode?)cs) },
        });
    }

    private static UsageSeries Series(string pod, double[] cpu, double[] mem) =>
        new(new ContainerKey("ns", pod, "main"), cpu.Select((c, i) => new UsagePoint(T0.AddMinutes(5 * i), c, mem[i])).ToList());

    private static UsageHistory History(IEnumerable<UsageSeries> series, double coverage = 1, double hours = 168, Dictionary<ContainerKey, ContainerExtras>? extras = null) =>
        new() { SourceName = "test", Window = TimeSpan.FromDays(7), Series = series.ToList(), Coverage = coverage, DataHours = hours, Extras = extras ?? [] };

    private static RightSizingEngine Engine(RightSizingSettings? s = null) => new(new KrrStrategy(), s ?? new RightSizingSettings());
    private static KubeObject? NoLookup(string k, string ns, string n) => null;

    private static ContainerReport One(KubeObject pod, UsageHistory h, RightSizingSettings? s = null) =>
        Engine(s).Analyze([pod], NoLookup, h).Containers.Single();

    [Fact]
    public void Percentiles_interpolate()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(50.5, Stats.Percentile(v, 50), 6);
        Assert.Equal(95.05, Stats.Percentile(v, 95), 6);
        Assert.Equal(100, Stats.Percentile(v, 100));
        Assert.Equal(0, Stats.Percentile([], 95));
        Assert.Equal(7, Stats.Percentile([7], 95));
    }

    [Theory]
    [InlineData(0.003, 0.005)]
    [InlineData(0.0051, 0.010)]
    [InlineData(0.104, 0.110)]
    [InlineData(0.101, 0.110)]
    [InlineData(0.55, 0.55)]
    [InlineData(0.551, 0.6)]
    [InlineData(2.0, 2.0)]
    public void Cpu_rounds_up_to_steps(double input, double expected) => Assert.Equal(expected, Rounding.Cpu(input), 6);

    [Theory]
    [InlineData(33, 40)]
    [InlineData(64, 64)]
    [InlineData(600, 608)]
    [InlineData(5000, 5120)]
    public void Memory_rounds_up_to_steps(double mi, double expectedMi) => Assert.Equal(expectedMi * Mi, Rounding.Memory(mi * Mi));

    [Fact]
    public void Over_provisioned_when_p95_below_40_percent_of_request()
    {
        var pod = Pod("p", cpuReq: "1", memReq: "1Gi");
        var r = One(pod, History([Series("p", [0.1, 0.12, 0.1, 0.11], [200 * Mi, 210 * Mi, 205 * Mi, 200 * Mi])]));
        Assert.Equal(Verdict.OverProvisioned, r.CpuVerdict);
        Assert.Equal(Verdict.OverProvisioned, r.MemoryVerdict);
        Assert.True(r.WasteCpuCores > 0.85);
        Assert.Equal(1, r.Replicas);
    }

    [Fact]
    public void Under_provisioned_when_p95_above_request()
    {
        var r = One(Pod("p", cpuReq: "100m", memReq: "100Mi"), History([Series("p", [0.5, 0.6, 0.55, 0.5], [300 * Mi, 310 * Mi, 305 * Mi, 300 * Mi])]));
        Assert.Equal(Verdict.UnderProvisioned, r.CpuVerdict);
        Assert.Equal(Verdict.UnderProvisioned, r.MemoryVerdict);
        Assert.Equal(0, r.WasteCpuCores);
    }

    [Fact]
    public void Ok_when_usage_is_close_to_request()
    {
        var r = One(Pod("p", cpuReq: "500m", memReq: "500Mi"), History([Series("p", [0.3, 0.35, 0.4, 0.3], [300 * Mi, 350 * Mi, 400 * Mi, 300 * Mi])]));
        Assert.Equal(Verdict.Ok, r.Primary);
    }

    [Fact]
    public void No_request_verdict()
    {
        var pod = Pod("p");
        pod.Json["spec"]!["containers"]![0]!["resources"] = new JsonObject();
        var r = One(pod, History([Series("p", [0.1], [10 * Mi])]));
        Assert.Equal(Verdict.NoRequest, r.CpuVerdict);
        Assert.Equal(Verdict.NoRequest, r.MemoryVerdict);
    }

    [Fact]
    public void Throttling_or_oomkill_means_under_provisioned_even_if_usage_looks_low()
    {
        var key = new ContainerKey("ns", "p", "main");
        var h = History([Series("p", [0.1, 0.1], [100 * Mi, 100 * Mi])], extras: new() { [key] = new ContainerExtras(0.25, 2) });
        var r = One(Pod("p", cpuReq: "200m", memReq: "256Mi"), h);
        Assert.Equal(Verdict.UnderProvisioned, r.CpuVerdict);
        Assert.Equal(Verdict.UnderProvisioned, r.MemoryVerdict);
        Assert.Equal(2, r.OomKills);
    }

    [Fact]
    public void Oomkill_from_pod_status_is_used_when_kube_state_metrics_is_absent()
    {
        var r = One(Pod("p", cpuReq: "200m", memReq: "256Mi", oom: true), History([Series("p", [0.1, 0.1], [100 * Mi, 100 * Mi])]));
        Assert.Equal(Verdict.UnderProvisioned, r.MemoryVerdict);
    }

    [Fact]
    public void Memory_limit_too_tight_when_max_reaches_90_percent_of_limit()
    {
        var r = One(Pod("p", cpuReq: "500m", memReq: "400Mi", memLim: "500Mi"), History([Series("p", [0.3, 0.3], [380 * Mi, 460 * Mi])]));
        Assert.Equal(Verdict.LimitTooTight, r.MemoryVerdict);
    }

    [Fact]
    public void Not_enough_data_when_coverage_or_hours_too_low()
    {
        var pod = Pod("p", cpuReq: "1");
        var s = Series("p", [0.01, 0.01], [10 * Mi, 10 * Mi]);
        Assert.Equal(Verdict.NotEnoughData, One(pod, History([s], coverage: 0.3)).CpuVerdict);
        Assert.Equal(Verdict.NotEnoughData, One(pod, History([s], hours: 5)).CpuVerdict);
        Assert.Equal(Verdict.OverProvisioned, One(pod, History([s])).CpuVerdict);
    }

    [Fact]
    public void Default_strategy_cpu_p95_memory_max_with_margin_and_rounding()
    {
        var cpu = Enumerable.Range(0, 100).Select(i => 0.1 + i * 0.001).ToArray();   // p95 ≈ 0.19405
        var mem = Enumerable.Range(0, 100).Select(i => (200 + i) * Mi).ToArray();    // max 299Mi
        var r = One(Pod("p", cpuReq: "1", memReq: "1Gi"), History([Series("p", cpu, mem)]));
        var rec = r.Recommended!;
        Assert.Equal(0.20, rec.CpuRequest, 6);                      // 194m → 200m
        Assert.Null(rec.CpuLimit);                                  // no CPU limit by default
        Assert.Equal(Rounding.Memory(299 * Mi * 1.15), rec.MemoryRequest);
        Assert.Equal(rec.MemoryRequest, rec.MemoryLimit);           // limit = request
        Assert.Contains("krr", rec.Strategy);
    }

    [Fact]
    public void Strategy_minimums_and_settings_apply()
    {
        var r = One(Pod("p", cpuReq: "1", memReq: "1Gi", cpuLim: "2"), History([Series("p", [0.001, 0.002], [1 * Mi, 2 * Mi])]),
            new RightSizingSettings { KeepCpuLimit = true, MemoryLimitFactor = 1.2 });
        Assert.Equal(0.010, r.Recommended!.CpuRequest, 6);
        Assert.Equal(32 * Mi, r.Recommended.MemoryRequest);
        Assert.Equal(2, r.Recommended.CpuLimit);
        Assert.Equal(Rounding.Memory(32 * Mi * 1.2), r.Recommended.MemoryLimit);
    }

    [Fact]
    public void Usage_is_aggregated_across_replicas_and_waste_multiplied()
    {
        var pods = new[] { Pod("a", "ReplicaSet", "web-1", cpuReq: "1"), Pod("b", "ReplicaSet", "web-1", cpuReq: "1"), Pod("c", "ReplicaSet", "web-1", cpuReq: "1") };
        var h = History([Series("a", [0.1], [100 * Mi]), Series("b", [0.2], [100 * Mi]), Series("c", [0.3], [100 * Mi])]);
        KubeObject? Lookup(string kind, string ns, string name) => kind == "ReplicaSet"
            ? new KubeObject(new JsonObject { ["kind"] = "ReplicaSet", ["metadata"] = new JsonObject { ["name"] = name, ["ownerReferences"] = new JsonArray((JsonNode?)new JsonObject { ["kind"] = "Deployment", ["name"] = "web", ["controller"] = true }) } })
            : new KubeObject(new JsonObject { ["kind"] = "Deployment", ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = new JsonObject { ["argocd.argoproj.io/instance"] = "web" } } });
        var report = Engine().Analyze(pods, Lookup, h);
        var w = Assert.Single(report.Workloads);
        Assert.Equal(("Deployment", "web", 3), (w.Kind, w.Name, w.Replicas));
        var c = w.Containers.Single();
        Assert.Equal(0.2, c.CpuP50, 6);
        Assert.Equal(0.3, c.CpuMax, 6);
        Assert.Equal((1 - 0.29) * 3 + 0, c.WasteCpuCores, 1); // p95 ≈ 0.29 → (1-0.29)*3 replicas
        Assert.Equal("Argo CD", c.GitOpsManager);
        Assert.Equal(3, report.CpuRequested);
    }

    [Fact]
    public void Workload_resolution_covers_job_cronjob_statefulset_and_bare_pods()
    {
        KubeObject? Lookup(string kind, string ns, string name) => kind == "Job"
            ? new KubeObject(new JsonObject { ["metadata"] = new JsonObject { ["name"] = name, ["ownerReferences"] = new JsonArray((JsonNode?)new JsonObject { ["kind"] = "CronJob", ["name"] = "nightly", ["controller"] = true }) } })
            : new KubeObject(new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } });
        Assert.Equal(("CronJob", "nightly"), Trim(RightSizingEngine.ResolveWorkload(Pod("p", "Job", "nightly-123"), Lookup)));
        Assert.Equal(("StatefulSet", "db"), Trim(RightSizingEngine.ResolveWorkload(Pod("db-0", "StatefulSet", "db"), Lookup)));
        Assert.Equal(("Pod", "lonely"), Trim(RightSizingEngine.ResolveWorkload(Pod("lonely"), Lookup)));
        static (string, string) Trim((string k, string n, KubeObject? o) t) => (t.k, t.n);
    }

    [Theory]
    [InlineData("argocd.argoproj.io/instance", "x", "Argo CD")]
    [InlineData("kustomize.toolkit.fluxcd.io/name", "x", "Flux")]
    [InlineData("app.kubernetes.io/managed-by", "Helm", "Helm")]
    [InlineData("app", "plain", null)]
    public void Gitops_detection(string label, string value, string? expected) =>
        Assert.Equal(expected, GitOps.Detect(new KubeObject(new JsonObject { ["metadata"] = new JsonObject { ["labels"] = new JsonObject { [label] = value } } })));

    [Fact]
    public void Cost_uses_price_per_hour_settings()
    {
        var r = One(Pod("p", cpuReq: "2", memReq: "4Gi"), History([Series("p", [0.5], [1024 * Mi])]), new RightSizingSettings { PricePerVcpuHour = 0.04, PricePerGibHour = 0.005 });
        Assert.Equal(1.5 * 0.04 + 3 * 0.005, r.WasteCostPerHour!.Value, 6);
    }

    [Fact]
    public void Snippets_yaml_helm_kustomize_and_exports()
    {
        var r = Engine().Analyze([Pod("p", cpuReq: "1", memReq: "1Gi")], NoLookup, History([Series("p", [0.1, 0.12], [200 * Mi, 210 * Mi])]));
        var cs = r.Containers.ToList();
        var yaml = Snippets.Yaml(cs);
        Assert.Contains("kind: Pod", yaml); Assert.Contains("resources:", yaml); Assert.Contains("requests:", yaml);
        var helm = Snippets.HelmValues(cs);
        Assert.Contains("resources:", helm); Assert.Contains("cpu:", helm);
        Assert.Contains("patch: |-", Snippets.KustomizePatch(cs));
        var diff = Snippets.Diff(cs[0]);
        Assert.Contains("-   requests.cpu: 1", diff); Assert.Contains("+   requests.cpu:", diff);
        Assert.StartsWith("cluster_source,namespace", Snippets.Csv(r));
        Assert.Contains("| ns | Pod/p | main |", Snippets.Markdown(r));
        var back = YamlJson.ParseDocuments(yaml);
        Assert.NotEmpty(back);
    }

    [Fact]
    public async Task Prometheus_source_reads_series_and_extras_in_batches_per_namespace()
    {
        await using var prom = await FakePrometheus.StartAsync();
        prom.Steady("ns", "p", "main", 0.2, 300 * Mi, throttled: 0.15, oom: 1).Steady("other", "q", "main", 0.1, 100 * Mi);
        var source = new PrometheusUsageSource(new HttpPromClient(new Uri(prom.Url)));
        Assert.Equal("Connected", await source.TestAsync(default));
        var h = await source.QueryAsync(UsageQuery.Default(7, ["ns", "other"]), default);
        Assert.Equal(2, h.Series.Count);
        Assert.True(h.Coverage > 0.95);
        Assert.True(h.DataHours > 160);
        Assert.Equal(0.15, h.Extras[new ContainerKey("ns", "p", "main")].ThrottledRatio);
        Assert.Equal(1, h.Extras[new ContainerKey("ns", "p", "main")].OomKills);
        // Batched: one query set per namespace.
        Assert.Equal(2, prom.Queries.Count(q => q.Contains("container_cpu_usage_seconds_total")));
        Assert.Contains(prom.Queries, q => q.Contains("namespace=~\"ns\""));
    }

    [Fact]
    public async Task Prometheus_with_bearer_token_provider()
    {
        await using var prom = await FakePrometheus.StartAsync();
        prom.RequiredBearer = "entra-token";
        var tokens = new FixedTokens("entra-token");
        var client = new HttpPromClient(new Uri(prom.Url), tokens, "https://prometheus.monitor.azure.com/.default");
        Assert.Equal("Connected", await new PrometheusUsageSource(client, "Azure Monitor").TestAsync(default));
        Assert.Equal("https://prometheus.monitor.azure.com/.default", tokens.LastScope);
        var bad = new PrometheusUsageSource(new HttpPromClient(new Uri(prom.Url)));
        await Assert.ThrowsAsync<KubeApiException>(() => bad.TestAsync(default));
    }

    private sealed class FixedTokens(string token) : ITokenProvider
    {
        public string? LastScope;
        public Task<string> GetTokenAsync(string scope, CancellationToken ct) { LastScope = scope; return Task.FromResult(token); }
    }

    [Fact]
    public async Task End_to_end_against_fake_cluster_and_prometheus()
    {
        await using var server = await FakeApiServer.StartAsync();
        await using var prom = await FakePrometheus.StartAsync();
        var pods = server.List(Gvr.Pods, "default").Where(p => p["metadata"]!["name"]!.GetValue<string>().StartsWith("web-")).ToList();
        foreach (var p in pods) prom.Steady("default", p["metadata"]!["name"]!.GetValue<string>(), "main", 0.02, 40 * Mi);
        var entry = KubeconfigCatalog.ParseFile(server.WriteKubeconfig())[0];
        await using var session = new ClusterSession(entry, new KubeTransportFactory(), new ClusterEventsService(), new SettingsStore());
        await session.ConnectAsync();
        using var lease = session.Cache.Subscribe(Gvr.Pods, "default");
        using var rs = session.Cache.Subscribe(Gvr.ReplicaSets, "default");
        using var dep = session.Cache.Subscribe(Gvr.Deployments, "default");
        while (lease.Informer.Count == 0 || rs.Informer.Count == 0 || dep.Informer.Count == 0) await Task.Delay(20);
        KubeObject? Lookup(string kind, string ns, string name)
        {
            var inf = kind switch { "ReplicaSet" => rs.Informer, "Deployment" => dep.Informer, _ => null };
            return inf?.Snapshot().Select(i => i.Object).FirstOrDefault(o => o.Name == name);
        }
        var history = await new PrometheusUsageSource(new HttpPromClient(new Uri(prom.Url))).QueryAsync(UsageQuery.Default(7, ["default"]), default);
        var report = Engine().Analyze(lease.Informer.Snapshot().Select(i => i.Object).Where(p => p.Name.StartsWith("web-")), Lookup, history);
        var w = Assert.Single(report.Workloads);
        Assert.Equal(("Deployment", "web", 3), (w.Kind, w.Name, w.Replicas));
        Assert.Equal(Verdict.OverProvisioned, w.Containers[0].CpuVerdict);
        Assert.Equal(0.02, w.Containers[0].Recommended!.CpuRequest, 3);
    }

    [Fact]
    public void Local_sampler_store_roundtrip_gaps_and_compaction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kubism-store-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new FileUsageStore(dir);
            var now = DateTimeOffset.UtcNow;
            var samples = new List<StoredSample>();
            for (var i = 0; i < 12; i++) samples.Add(new StoredSample(now.AddMinutes(-60 + i * 5), "ns", "p", "main", 0.1 * (i + 1), 100 * Mi));
            store.Append("c1", samples);
            // Old day: 30s samples, to be downsampled.
            var old = now.AddDays(-3).Date.AddHours(12);
            store.Append("c1", Enumerable.Range(0, 20).Select(i => new StoredSample(old.AddSeconds(30 * i), "ns", "p", "main", 0.2, (100 + i) * Mi)).ToList());
            Assert.Equal(12, store.Read("c1", now.AddHours(-2), now).Count);
            store.Compact("c1", now, TimeSpan.FromDays(30));
            var compacted = store.Read("c1", old.AddMinutes(-1), old.AddHours(1));
            Assert.True(compacted.Count < 20);
            Assert.Equal(119 * Mi, compacted.Max(s => s.Memory)); // memory keeps the max
            var src = new LocalSamplerSource("c1", store);
            var h = src.QueryAsync(UsageQuery.Default(1), default).Result;
            Assert.Single(h.Series);
            Assert.True(h.Coverage < 0.2); // only ~1 hour of a 24 h window: the gap is visible
            store.Compact("c1", now.AddDays(40), TimeSpan.FromDays(30));
            Assert.Empty(store.Read("c1", now.AddDays(-5), now));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Vpa_source_returns_targets_without_series()
    {
        var t = new FakeKubeTransport();
        t.Set(new Gvr("autoscaling.k8s.io", "v1", "verticalpodautoscalers"), JsonNode.Parse("""
            {"metadata":{"name":"web","namespace":"ns","uid":"v1"},"spec":{"targetRef":{"kind":"Deployment","name":"web"}},
             "status":{"recommendation":{"containerRecommendations":[{"containerName":"main","target":{"cpu":"250m","memory":"300Mi"},"lowerBound":{"cpu":"100m","memory":"200Mi"},"upperBound":{"cpu":"1","memory":"1Gi"}}]}}}
            """)!.AsObject());
        var h = await new VpaRecommendationSource(t).QueryAsync(UsageQuery.Default(), default);
        var v = Assert.Single(h.Vpa);
        Assert.Equal(0.25, v.CpuTarget, 6);
        Assert.Equal(300 * Mi, v.MemoryTarget);
        var pod = Pod("web-abc", "Deployment", "web");
        var report = Engine().Analyze([pod], NoLookup, h);
        // Owner lookup fails so the workload is the Deployment by name through owner reference.
        Assert.Equal("VPA recommender target", report.Containers.Single().Recommended!.Strategy);
    }
}
