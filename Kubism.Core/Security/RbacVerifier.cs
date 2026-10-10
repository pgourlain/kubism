using System.Text.Json.Nodes;
using Kubism.Core.Actions;
using Kubism.Core.Api;

namespace Kubism.Core.Security;

/// <summary>Who the API server thinks you are (SelfSubjectReview).</summary>
public sealed record RbacIdentity(string UserName, IReadOnlyList<string> Groups)
{
    /// <summary>A service account's user name is system:serviceaccount:ns:name.</summary>
    public RbacSubject Subject => RbacSubject.Parse(UserName) is { Kind: RbacSubject.ServiceAccount } sa ? sa : new RbacSubject(RbacSubject.User, UserName);
}

/// <param name="Computed">What the RBAC objects say (null when the identity could not be established).</param>
/// <param name="Server">What the API server answered; null when the review could not be asked.</param>
/// <param name="Mismatch">Both answers exist and differ; null when one is missing.</param>
public sealed record RbacVerification(RbacRequest Request, bool? Computed, bool? Server, bool? Mismatch, string? Reason, string Note);

/// <summary>
/// Checks the graph's answer with the API server: SelfSubjectAccessReview for the current user, and SubjectAccessReview for another subject
/// only when the current user may create them. A difference is information, not an error: webhook authorizers, Azure RBAC for Kubernetes and
/// the Node authorizer all answer outside the RBAC objects.
/// </summary>
public sealed class RbacVerifier(IKubeTransport transport, AccessReview access)
{
    private static readonly Gvr SubjectAccessReviews = new("authorization.k8s.io", "v1", "subjectaccessreviews");
    private RbacIdentity? _identity;
    private bool _identityTried;

    /// <summary>The user as the API server sees them (SelfSubjectReview, Kubernetes 1.28+); null when it cannot be asked. Cached per session.</summary>
    public async Task<RbacIdentity?> WhoAmIAsync(CancellationToken ct)
    {
        if (_identityTried) return _identity;
        try
        {
            var r = await transport.SendAsync("POST", "/apis/authentication.k8s.io/v1/selfsubjectreviews",
                new JsonObject { ["apiVersion"] = "authentication.k8s.io/v1", ["kind"] = "SelfSubjectReview" }.ToJsonString(), "application/json", ct).ConfigureAwait(false);
            if (r.StatusCode is >= 200 and < 300 && r.Json is JsonObject o && o["status"]?["userInfo"] is JsonObject ui && KubeObject.Str(ui, "username") is { Length: > 0 } user)
                _identity = new RbacIdentity(user, RbacJson.Strings(ui["groups"]));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* unsupported or forbidden: the verification says so */ }
        _identityTried = true;
        return _identity;
    }

    public async Task<RbacVerification> VerifySelfAsync(RbacGraph graph, RbacRequest request, CancellationToken ct)
    {
        var id = await WhoAmIAsync(ct).ConfigureAwait(false);
        bool? computed = id is null ? null : graph.Explain(id.Subject, request, id.Groups).Allowed;
        var (server, reason) = await ReviewAsync("/apis/authorization.k8s.io/v1/selfsubjectaccessreviews", request, null, ct).ConfigureAwait(false);
        return Compare(request, computed, server, reason, id is null ? "Your identity could not be read (SelfSubjectReview), so the RBAC objects were not compared." : null);
    }

    /// <summary>Whether SubjectAccessReview may be used for other subjects: the current user needs <c>create subjectaccessreviews</c>.</summary>
    public Task<AccessResult> CanReviewOthersAsync(CancellationToken ct) => access.CanAsync("create", SubjectAccessReviews, null, null, ct);

    public async Task<RbacVerification> VerifySubjectAsync(RbacGraph graph, RbacSubject subject, RbacRequest request, CancellationToken ct)
    {
        var can = await CanReviewOthersAsync(ct).ConfigureAwait(false);
        var computed = graph.Explain(subject, request).Allowed;
        if (!can.Allowed) return new RbacVerification(request, computed, null, null, null, "Not compared: you cannot create subjectaccessreviews.");
        var groups = subject.Kind == RbacSubject.ServiceAccount ? new[] { "system:serviceaccounts", $"system:serviceaccounts:{subject.Namespace}", "system:authenticated" } : subject.Kind == RbacSubject.Group ? [subject.Name] : new[] { "system:authenticated" };
        var user = new JsonObject { ["user"] = subject.Kind == RbacSubject.Group ? "" : subject.UserName, ["groups"] = new JsonArray(groups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) };
        var (server, reason) = await ReviewAsync("/apis/authorization.k8s.io/v1/subjectaccessreviews", request, user, ct).ConfigureAwait(false);
        return Compare(request, computed, server, reason, null);
    }

    private static RbacVerification Compare(RbacRequest request, bool? computed, bool? server, string? reason, string? extraNote)
    {
        bool? mismatch = computed is null || server is null ? null : computed != server;
        var note = extraNote ?? (server is null ? "The API server could not be asked (SelfSubjectAccessReview failed)."
            : mismatch == false ? "The API server agrees with the RBAC objects."
            : mismatch == true && computed == true ? "The RBAC objects allow this but the API server refuses: another authorizer (webhook, Azure RBAC for Kubernetes) or an admission step says no."
            : mismatch == true ? "The API server allows this but no RBAC rule does: it comes from another authorizer (Node, webhook, Azure RBAC for Kubernetes), or from a group the RBAC objects do not show."
            : "Not compared.");
        return new RbacVerification(request, computed, server, mismatch, reason, note);
    }

    private async Task<(bool? Allowed, string? Reason)> ReviewAsync(string path, RbacRequest request, JsonObject? userSpec, CancellationToken ct)
    {
        var spec = new JsonObject();
        if (userSpec is not null) foreach (var kv in userSpec) spec[kv.Key] = kv.Value?.DeepClone();
        if (request.IsNonResource) spec["nonResourceAttributes"] = new JsonObject { ["path"] = request.NonResourceUrl, ["verb"] = request.Verb };
        else spec["resourceAttributes"] = new JsonObject
        {
            ["verb"] = request.Verb, ["group"] = request.Group ?? "", ["resource"] = request.Resource, ["subresource"] = request.Subresource,
            ["namespace"] = request.Namespace, ["name"] = request.Name,
        };
        var kind = userSpec is null ? "SelfSubjectAccessReview" : "SubjectAccessReview";
        try
        {
            var r = await transport.SendAsync("POST", path, new JsonObject { ["apiVersion"] = "authorization.k8s.io/v1", ["kind"] = kind, ["spec"] = spec }.ToJsonString(), "application/json", ct).ConfigureAwait(false);
            if (r.StatusCode is < 200 or >= 300 || (r.Json as JsonObject)?["status"] is not JsonObject status) return (null, null);
            return (status["allowed"]?.GetValue<bool>(), KubeObject.Str(status, "reason") ?? KubeObject.Str(status, "evaluationError"));
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }
}
