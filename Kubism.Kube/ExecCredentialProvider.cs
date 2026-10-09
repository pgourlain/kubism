using System.Diagnostics;
using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Kube;

public sealed record ExecSpec(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string? ApiVersion, bool Interactive);

/// <summary>Runs a kubeconfig exec plugin, captures stderr (device-code prompts) and caches the token until just before it expires.</summary>
public sealed class ExecCredentialProvider(ExecSpec spec, IExecPluginObserver? observer)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset? _expires;

    public async Task<string?> GetTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _token is not null && (_expires is null || _expires > DateTimeOffset.UtcNow.AddSeconds(60))) return _token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _token is not null && (_expires is null || _expires > DateTimeOffset.UtcNow.AddSeconds(60))) return _token;
            (_token, _expires) = await RunAsync(ct).ConfigureAwait(false);
            return _token;
        }
        finally { _lock.Release(); }
    }

    private async Task<(string? token, DateTimeOffset? expires)> RunAsync(CancellationToken ct)
    {
        var exe = ShellEnvironment.Which(spec.Command)
                  ?? throw new KubeConnectionException($"Exec plugin '{spec.Command}' not found. PATH searched: {Environment.GetEnvironmentVariable("PATH")}");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, UseShellExecute = false,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Env) psi.Environment[k] = v;
        var apiVersion = spec.ApiVersion ?? "client.authentication.k8s.io/v1";
        psi.Environment["KUBERNETES_EXEC_INFO"] = new JsonObject
        {
            ["apiVersion"] = apiVersion, ["kind"] = "ExecCredential",
            ["spec"] = new JsonObject { ["interactive"] = spec.Interactive },
        }.ToJsonString();

        using var p = new Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            observer?.OnPluginOutput(e.Data);
        };
        try { p.Start(); }
        catch (Exception ex) { throw new KubeConnectionException($"Exec plugin '{spec.Command}' could not start: {ex.Message}", ex); }
        p.BeginErrorReadLine();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            string err; lock (stderr) err = stderr.ToString().Trim();
            throw new KubeConnectionException($"Exec plugin '{spec.Command}' failed (exit {p.ExitCode}): {err}");
        }
        var json = JsonNode.Parse(output) as JsonObject ?? throw new KubeConnectionException($"Exec plugin '{spec.Command}' returned invalid output");
        var status = json["status"] as JsonObject ?? throw new KubeConnectionException($"Exec plugin '{spec.Command}' returned no status");
        var token = status["token"]?.GetValue<string>();
        DateTimeOffset? exp = status["expirationTimestamp"]?.GetValue<string>() is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
        return (token, exp);
    }
}

/// <summary>Adds the bearer token produced by the exec plugin, and refreshes it once on 401.</summary>
public sealed class ExecAuthHandler(ExecCredentialProvider provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await provider.GetTokenAsync(ct).ConfigureAwait(false);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && request.Content is null)
        {
            token = await provider.GetTokenAsync(ct, forceRefresh: true).ConfigureAwait(false);
            if (token is not null)
            {
                response.Dispose();
                using var retry = await CloneAsync(request).ConfigureAwait(false);
                retry.Headers.Authorization = new("Bearer", token);
                return await base.SendAsync(retry, ct).ConfigureAwait(false);
            }
        }
        return response;
    }

    private static Task<HttpRequestMessage> CloneAsync(HttpRequestMessage r)
    {
        var c = new HttpRequestMessage(r.Method, r.RequestUri);
        foreach (var h in r.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return Task.FromResult(c);
    }
}
