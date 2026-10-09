using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Actions;

public sealed record AccessResult(bool Allowed, string? Reason);

/// <summary>SelfSubjectAccessReview with a 5 minute per-session cache.</summary>
public sealed class AccessReview(IKubeTransport transport)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset at, AccessResult result)> _cache = new();
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    public async Task<AccessResult> CanAsync(string verb, Gvr gvr, string? ns, string? name, CancellationToken ct, string? subresource = null)
    {
        var key = $"{verb}|{gvr}|{ns}|{name}|{subresource}";
        if (_cache.TryGetValue(key, out var c) && DateTimeOffset.UtcNow - c.at < Ttl) return c.result;

        var body = new JsonObject
        {
            ["apiVersion"] = "authorization.k8s.io/v1",
            ["kind"] = "SelfSubjectAccessReview",
            ["spec"] = new JsonObject
            {
                ["resourceAttributes"] = new JsonObject
                {
                    ["verb"] = verb, ["group"] = gvr.Group, ["resource"] = gvr.Resource,
                    ["namespace"] = ns, ["name"] = name, ["subresource"] = subresource,
                },
            },
        };
        AccessResult result;
        try
        {
            var r = await transport.SendAsync("POST", "/apis/authorization.k8s.io/v1/selfsubjectaccessreviews", body.ToJsonString(), "application/json", ct).ConfigureAwait(false);
            if (r.StatusCode is < 200 or >= 300) result = new AccessResult(true, null); // can't tell: don't block
            else
            {
                var status = (r.Json as JsonObject)?["status"] as JsonObject;
                var allowed = status?["allowed"]?.GetValue<bool>() ?? true;
                result = allowed ? new AccessResult(true, null)
                    : new AccessResult(false, $"forbidden: you cannot {verb} {gvr.Resource}{(ns is null ? "" : $" in namespace {ns}")}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { result = new AccessResult(true, null); }
        _cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    public void Clear() => _cache.Clear();
}
