using Kubism.Core.Api;
using Kubism.Core.Cache;
using Kubism.Core.Catalog;
using Kubism.Core.Discovery;
using Kubism.Kube;
using Xunit;

namespace Kubism.Integration.Tests;

public sealed class RequiresClusterFactAttribute : FactAttribute
{
    public RequiresClusterFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KUBISM_IT_KUBECONFIG") is null) Skip = "KUBISM_IT_KUBECONFIG is not set";
    }
}

/// <summary>Runs against a real cluster (CI: kind). Read-only except for one namespaced ConfigMap in a throwaway namespace.</summary>
public class IntegrationTests
{
    private static KubeTransport Connect()
    {
        var path = Environment.GetEnvironmentVariable("KUBISM_IT_KUBECONFIG")!;
        path = path.StartsWith("~") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
        var ctx = Environment.GetEnvironmentVariable("KUBISM_IT_CONTEXT");
        var entries = KubeconfigCatalog.ParseFile(path);
        return new KubeTransport(ctx is null ? entries[0] : entries.First(e => e.ContextName == ctx));
    }

    [RequiresClusterFact]
    public async Task Smoke_version()
    {
        using var t = Connect();
        var v = await t.GetAsync("/version", TestContext.Current.CancellationToken);
        Assert.StartsWith("v1.", v!["gitVersion"]!.ToString());
    }

    [RequiresClusterFact]
    public async Task Discovery_lists_core_kinds_and_pods_table_has_status_column()
    {
        using var t = Connect();
        var d = new ApiDiscovery(t);
        await d.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(d.FindByName("pods"));
        Assert.NotNull(d.FindByName("deployments"));
        var l = await t.ListAsync(Gvr.Pods, "kube-system", new ListOptions { AsTable = true, Limit = 50 }, TestContext.Current.CancellationToken);
        Assert.Contains(l.Columns!, c => c.Name == "Status");
        Assert.NotEmpty(l.Items);
    }

    [RequiresClusterFact]
    public async Task Informer_lists_and_watches_kube_system_pods()
    {
        using var t = Connect();
        await using var cache = new ResourceCache(t, "it");
        using var lease = cache.Subscribe(Gvr.Pods, "kube-system");
        var until = DateTime.UtcNow.AddSeconds(30);
        while (lease.Informer.Count == 0 && DateTime.UtcNow < until) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(lease.Informer.Count > 0);
        Assert.Equal(InformerState.Live, lease.Informer.State);
    }

    [RequiresClusterFact]
    public async Task Exec_into_a_kube_system_pod_that_has_a_shell()
    {
        using var t = Connect();
        var pods = await t.ListAsync(Gvr.Pods, "kube-system", new ListOptions { FieldSelector = "status.phase=Running" }, TestContext.Current.CancellationToken);
        // Distroless images (etcd, apiserver…) have no shell: try the candidates until one answers.
        var tried = new List<string>();
        foreach (var pod in pods.Items.Select(i => new KubeObject(i)).Where(p => p.Name.StartsWith("kube-proxy") || p.Name.StartsWith("kindnet") || p.Name.StartsWith("coredns")))
        {
            await using var exec = new Kubism.Core.Exec.ExecSession(t, "kube-system", pod.Name, null, ["/bin/sh", "-c", "echo kubism-ok"], tty: false);
            var output = new System.Text.StringBuilder();
            exec.Output += (d, _) => output.Append(System.Text.Encoding.UTF8.GetString(d));
            var done = new TaskCompletionSource<Kubism.Core.Exec.ExecResult>();
            exec.Exited += r => done.TrySetResult(r);
            await exec.StartAsync(TestContext.Current.CancellationToken);
            var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            tried.Add($"{pod.Name}: {result.Message}");
            if (result.Success && output.ToString().Contains("kubism-ok")) return;
        }
        // Nothing with a shell on this distribution: don't fail the suite, but say so.
        Assert.True(tried.Count == 0 || tried.All(m => m.Contains("not found") || m.Contains("executable")), "exec failed on every candidate: " + string.Join(" | ", tried));
    }
}
