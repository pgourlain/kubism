using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kubism.Testing;

public sealed record PromProfile(string Namespace, string Pod, string Container, Func<double, double> Cpu, Func<double, double> Memory, double Throttled = 0, int OomKills = 0);

/// <summary>Prometheus HTTP API with canned series for right-sizing tests and screenshots.</summary>
public sealed class FakePrometheus : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; private set; } = "";
    public List<PromProfile> Profiles { get; } = [];
    public List<string> Queries { get; } = [];
    public string? RequiredBearer { get; set; }
    public TimeSpan DataSpan { get; set; } = TimeSpan.FromDays(7);

    private FakePrometheus(WebApplication app) => _app = app;

    public static async Task<FakePrometheus> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var p = new FakePrometheus(app);
        app.Run(p.HandleAsync);
        await app.StartAsync();
        p.Url = app.Urls.First().Replace("localhost", "127.0.0.1");
        return p;
    }

    /// <summary>Convenience: a container using a steady fraction of given values.</summary>
    public FakePrometheus Steady(string ns, string pod, string container, double cpu, double memBytes, double throttled = 0, int oom = 0)
    {
        Profiles.Add(new PromProfile(ns, pod, container, _ => cpu, _ => memBytes, throttled, oom));
        return this;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (RequiredBearer is not null && ctx.Request.Headers.Authorization != $"Bearer {RequiredBearer}")
        {
            ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("unauthorized"); return;
        }
        var q = ctx.Request.Query["query"].ToString();
        lock (Queries) Queries.Add(q);
        var path = ctx.Request.Path.Value!;
        JsonObject result;
        if (path == "/api/v1/query_range") result = Range(q, ctx);
        else if (path == "/api/v1/query") result = Instant(q);
        else { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(result.ToJsonString());
    }

    private IEnumerable<PromProfile> Select(string q)
    {
        var m = Regex.Match(q, "namespace=~\"([^\"]*)\"");
        if (!m.Success) return Profiles;
        var allowed = m.Groups[1].Value.Split('|').Select(Regex.Unescape).ToHashSet();
        return Profiles.Where(p => allowed.Contains(p.Namespace));
    }

    private static JsonObject Metric(PromProfile p) => new() { ["namespace"] = p.Namespace, ["pod"] = p.Pod, ["container"] = p.Container };

    private JsonObject Range(string q, HttpContext ctx)
    {
        var cpu = q.Contains("container_cpu_usage_seconds_total");
        var end = long.Parse(ctx.Request.Query["end"]!); var start = long.Parse(ctx.Request.Query["start"]!); var step = long.Parse(ctx.Request.Query["step"]!);
        var first = Math.Max(start, end - (long)DataSpan.TotalSeconds);
        var results = new JsonArray();
        foreach (var p in Select(q))
        {
            var values = new JsonArray();
            for (var t = first; t <= end; t += step)
            {
                var x = (t - first) / 3600.0;
                var v = cpu ? p.Cpu(x) : p.Memory(x);
                values.Add((JsonNode?)new JsonArray((JsonNode?)JsonValue.Create(t), JsonValue.Create(v.ToString("R", CultureInfo.InvariantCulture))));
            }
            results.Add((JsonNode?)new JsonObject { ["metric"] = Metric(p), ["values"] = values });
        }
        return new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["resultType"] = "matrix", ["result"] = results } };
    }

    private JsonObject Instant(string q)
    {
        var results = new JsonArray();
        if (q == "vector(1)")
            results.Add((JsonNode?)new JsonObject { ["metric"] = new JsonObject(), ["value"] = new JsonArray((JsonNode?)JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), JsonValue.Create("1")) });
        else if (q.Contains("cfs_throttled") || q.Contains("OOMKilled"))
        {
            var thr = q.Contains("cfs_throttled");
            foreach (var p in Select(q))
            {
                var v = thr ? p.Throttled : p.OomKills;
                if (v == 0) continue;
                results.Add((JsonNode?)new JsonObject { ["metric"] = Metric(p), ["value"] = new JsonArray((JsonNode?)JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), JsonValue.Create(v.ToString(CultureInfo.InvariantCulture))) });
            }
        }
        return new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["resultType"] = "vector", ["result"] = results } };
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(new CancellationTokenSource(1000).Token); } catch { }
        await _app.DisposeAsync();
    }
}
