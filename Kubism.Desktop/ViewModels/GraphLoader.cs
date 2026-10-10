using System.Text.Json.Nodes;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Helm;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

/// <summary>Loads what the relation graph of an object (or of a Helm release) needs and builds it. Secret values never leave the list response.</summary>
public static class GraphLoader
{
    private static readonly (Gvr gvr, string resource)[] Kinds =
    [
        (Gvr.Pods, "pods"), (Gvr.ReplicaSets, "replicasets"), (Gvr.Deployments, "deployments"), (Gvr.StatefulSets, "statefulsets"), (Gvr.DaemonSets, "daemonsets"),
        (Gvr.Jobs, "jobs"), (Gvr.CronJobs, "cronjobs"), (Gvr.Services, "services"), (new Gvr("networking.k8s.io", "v1", "ingresses"), "ingresses"),
        (Gvr.ConfigMaps, "configmaps"), (new Gvr("", "v1", "persistentvolumeclaims"), "persistentvolumeclaims"),
        (new Gvr("autoscaling", "v2", "horizontalpodautoscalers"), "horizontalpodautoscalers"), (new Gvr("", "v1", "serviceaccounts"), "serviceaccounts"),
    ];

    public static async Task<RelationGraph> LoadAsync(WorkspaceViewModel ws, KubeObject root, CancellationToken ct)
    {
        var t = ws.Session.Transport;
        var disc = ws.Session.Discovery;
        var candidates = new List<KubeObject>();
        async Task Load(Gvr gvr, string? ns, ListOptions? opts = null)
        {
            try
            {
                var l = await t.ListAsync(gvr, ns, opts ?? new ListOptions(), ct).ConfigureAwait(false);
                lock (candidates)
                    foreach (var i in l.Items)
                    {
                        // Only names and labels matter here: values never leave the list response.
                        if (SecretMasker.IsSecret(i)) { i.Remove("data"); i.Remove("stringData"); }
                        candidates.Add(new KubeObject(i));
                    }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* forbidden or missing kinds simply have no edges */ }
        }
        bool Has(string resource) => disc.Kinds.Count == 0 || disc.Kinds.Any(k => k.Resource == resource && k.CanList);

        if (root.Namespace is { } ns)
        {
            var secrets = Has("secrets");
            await Task.WhenAll(Kinds.Where(k => Has(k.resource)).Select(k => Load(k.gvr, ns))
                // Config secrets without the (large) Helm release payloads, which are read separately for their labels only.
                .Concat(secrets ? [Load(Gvr.Secrets, ns, new ListOptions { FieldSelector = $"type!={HelmReleaseInfo.SecretType}" }), Load(Gvr.Secrets, ns, new ListOptions { LabelSelector = "owner=helm" })] : [])).ConfigureAwait(false);
            if (candidates.Any(c => c.Kind == "PersistentVolumeClaim") && Has("persistentvolumes")) await Load(new Gvr("", "v1", "persistentvolumes"), null).ConfigureAwait(false);
            if (candidates.Any(c => c.Kind == "Pod" && c.StringAt("spec", "nodeName") is not null) && Has("nodes")) await Load(Gvr.Nodes, null).ConfigureAwait(false);
        }
        else if (root.Kind == "Node")
            await Load(Gvr.Pods, null, new ListOptions { FieldSelector = $"spec.nodeName={root.Name}", Limit = 200 }).ConfigureAwait(false);
        else if (root.Kind == "PersistentVolume" && root.At("spec", "claimRef") is JsonObject claim && claim["namespace"]?.ToString() is { } cns)
            await Load(new Gvr("", "v1", "persistentvolumeclaims"), cns).ConfigureAwait(false);
        var releases = HelmReleaseInfo.FromSecrets(candidates);
        return RelationGraph.Build(root, candidates.DistinctBy(c => RelationGraph.IdOf(c)), releases: releases);
    }
}
