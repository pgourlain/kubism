using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kubism.Core.Api;

namespace Kubism.Core.Usage;

/// <summary>
/// <c>Used</c>: something Kubism read refers to it. <c>Unused</c>: nothing it read does (it may still be read by something outside the
/// cluster objects: say "not referenced", not "useless"). <c>External</c>: nothing refers to it, but the cluster itself manages it
/// (Helm release data, service account tokens, objects owned by a controller…).
/// </summary>
public enum UsageStatus { Used, Unused, External }

/// <param name="How">"volume", "env", "envFrom", "imagePullSecrets", "TLS", "claim"…</param>
public sealed record Consumer(string Kind, string? Namespace, string Name, string How)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

public sealed record UsageRow(string Kind, string? Namespace, string Name, UsageStatus Status, IReadOnlyList<Consumer> Consumers, string Note)
{
    public string Display => Namespace is null ? Name : $"{Namespace}/{Name}";
    public int Count => Consumers.Count;

    /// <summary>"Deployment/web (env), Pod/job-x (volume) and 3 more".</summary>
    public string UsedBy
    {
        get
        {
            if (Consumers.Count == 0) return "";
            var head = string.Join(", ", Consumers.Take(3).Select(c => $"{c.Display} ({c.How})"));
            return Consumers.Count > 3 ? $"{head} and {Consumers.Count - 3} more" : head;
        }
    }
}

/// <summary>A consumer names an object that is not in the cluster (or, for a pod, a claim, secret or config map it needs to start).</summary>
public sealed record BrokenReference(Consumer Consumer, string TargetKind, string? TargetNamespace, string TargetName, string Detail)
{
    public string Target => TargetNamespace is null ? $"{TargetKind}/{TargetName}" : $"{TargetKind}/{TargetNamespace}/{TargetName}";
}

/// <summary>
/// Which ConfigMaps, Secrets, PersistentVolumeClaims, PersistentVolumes and StorageClasses are referenced, by whom, and which references point
/// at nothing. Pure logic over objects already loaded. Only names are read: no Secret value is touched. Controllers are read through their pod
/// template and the pods they own are skipped, so one reference is counted once.
/// </summary>
public sealed class ResourceUsage
{
    public const string ConfigMap = "ConfigMap", Secret = "Secret", Pvc = "PersistentVolumeClaim", Pv = "PersistentVolume", StorageClass = "StorageClass";
    public static readonly IReadOnlyList<string> AnalyzedKinds = [ConfigMap, Secret, Pvc, Pv, StorageClass];
    /// <summary>Kinds that hold what refers to the analyzed ones. A missing one means "unused" cannot be trusted.</summary>
    public static readonly IReadOnlyList<string> ConsumerKinds = ["Pod", "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "Job", "CronJob", "Ingress", "ServiceAccount"];
    private static readonly HashSet<string> ControllerKinds = ["ReplicaSet", "StatefulSet", "DaemonSet", "Job", "ReplicationController"];

    private ResourceUsage(IReadOnlyList<UsageRow> rows, IReadOnlyList<BrokenReference> broken) { Rows = rows; Broken = broken; }

    public IReadOnlyList<UsageRow> Rows { get; }
    public IReadOnlyList<BrokenReference> Broken { get; }

    /// <param name="listedKinds">Kinds that were really listed (a forbidden list is not an empty one): a reference to a kind that was not listed is never reported as broken.</param>
    public static ResourceUsage Analyze(IEnumerable<KubeObject> objects, IReadOnlySet<string> listedKinds)
    {
        var all = objects.ToList();
        var byKind = all.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.ToList());
        List<KubeObject> Of(string kind) => byKind.TryGetValue(kind, out var l) ? l : [];

        var refs = new List<Ref>();
        foreach (var o in all)
        {
            if (o.Kind == "Ingress")
                foreach (var tls in o.At("spec", "tls") as JsonArray ?? [])
                    if (tls?["secretName"]?.ToString() is { Length: > 0 } s) refs.Add(new Ref(Secret, o.Namespace, s, new Consumer(o.Kind, o.Namespace, o.Name, "TLS"), false));
            if (o.Kind == "ServiceAccount")
            {
                foreach (var s in o.At("imagePullSecrets") as JsonArray ?? [])
                    if (s?["name"]?.ToString() is { Length: > 0 } n) refs.Add(new Ref(Secret, o.Namespace, n, new Consumer(o.Kind, o.Namespace, o.Name, "imagePullSecrets"), false));
                foreach (var s in o.At("secrets") as JsonArray ?? [])
                    if (s?["name"]?.ToString() is { Length: > 0 } n) refs.Add(new Ref(Secret, o.Namespace, n, new Consumer(o.Kind, o.Namespace, o.Name, "serviceaccount"), true));
            }
            if (!IsConsumer(o) || PodSpecOf(o) is not { } spec) continue;
            var consumer = (string how) => new Consumer(o.Kind, o.Namespace, o.Name, how);
            foreach (var (kind, name, how, optional) in PodSpecRefs(spec)) refs.Add(new Ref(kind, o.Namespace, name, consumer(how), optional));
            if (o.Kind == "StatefulSet") AddClaimTemplates(o, Of(Pvc), refs);
        }

        // PVC -> StorageClass / PV; PV -> StorageClass / PVC.
        var defaultClass = Of(StorageClass).FirstOrDefault(IsDefaultClass)?.Name;
        foreach (var pvc in Of(Pvc))
        {
            var consumer = new Consumer(Pvc, pvc.Namespace, pvc.Name, "storageClass");
            var sc = pvc.StringAt("spec", "storageClassName");
            if (sc is null && pvc.At("spec", "storageClassName") is null) sc = defaultClass; // unset = the default class; "" = none on purpose
            if (!string.IsNullOrEmpty(sc)) refs.Add(new Ref(StorageClass, null, sc, consumer, false));
            if (pvc.StringAt("spec", "volumeName") is { Length: > 0 } pvName) refs.Add(new Ref(Pv, null, pvName, new Consumer(Pvc, pvc.Namespace, pvc.Name, "bound volume"), false));
        }
        foreach (var pv in Of(Pv))
        {
            if (pv.StringAt("spec", "storageClassName") is { Length: > 0 } sc) refs.Add(new Ref(StorageClass, null, sc, new Consumer(Pv, null, pv.Name, "storageClass"), false));
            // A PV's claim is a use of the PV; a claim that vanished leaves the PV bound to nothing.
            if (pv.StringAt("spec", "claimRef", "name") is { Length: > 0 } cn && pv.StringAt("spec", "claimRef", "namespace") is { } cns && Of(Pvc).Any(c => c.Namespace == cns && c.Name == cn))
                refs.Add(new Ref(Pv, null, pv.Name, new Consumer(Pvc, cns, cn, "claim"), false));
        }

        var exists = new Dictionary<string, HashSet<(string?, string)>>();
        foreach (var kind in AnalyzedKinds) exists[kind] = Of(kind).Select(o => (o.Namespace, o.Name)).ToHashSet();

        var consumers = new Dictionary<(string, string?, string), List<Consumer>>();
        var broken = new List<BrokenReference>();
        foreach (var r in refs)
        {
            var key = (r.Kind, r.Namespace, r.Name);
            if (!exists[r.Kind].Contains((r.Namespace, r.Name)))
            {
                if (r.Optional || !listedKinds.Contains(r.Kind)) continue;
                broken.Add(new BrokenReference(r.Consumer, r.Kind, r.Namespace, r.Name, $"{r.Consumer.Display} refers to {r.Kind} {r.Name} ({r.Consumer.How}) which does not exist"));
                continue;
            }
            if (!consumers.TryGetValue(key, out var list)) consumers[key] = list = [];
            if (!list.Contains(r.Consumer)) list.Add(r.Consumer);
        }

        var rows = new List<UsageRow>();
        foreach (var kind in AnalyzedKinds)
            foreach (var o in Of(kind))
            {
                var used = consumers.TryGetValue((kind, o.Namespace, o.Name), out var c) ? c : [];
                var (status, note) = Classify(o, used, defaultClass);
                rows.Add(new UsageRow(kind, o.Namespace, o.Name, status, used.OrderBy(x => x.Display, StringComparer.Ordinal).ToList(), note));
            }
        rows.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
        broken.Sort((a, b) => string.CompareOrdinal(a.Consumer.Display, b.Consumer.Display));
        return new ResourceUsage(rows, broken);
    }

    private sealed record Ref(string Kind, string? Namespace, string Name, Consumer Consumer, bool Optional);

    // ---------- classification ----------

    private static (UsageStatus, string) Classify(KubeObject o, List<Consumer> used, string? defaultClass)
    {
        switch (o.Kind)
        {
            case Pv:
                var phase = o.StringAt("status", "phase");
                var policy = o.StringAt("spec", "persistentVolumeReclaimPolicy") ?? "";
                if (used.Count > 0 || phase == "Bound")
                    return (UsageStatus.Used, o.StringAt("spec", "claimRef", "name") is { } cn ? $"Bound to {o.StringAt("spec", "claimRef", "namespace")}/{cn}" : "");
                return (UsageStatus.Unused, phase switch
                {
                    "Released" => $"Released: its claim was deleted, reclaim policy {policy}. The data is still on the volume.",
                    "Available" => "Available: no claim has bound it",
                    "Failed" => "Failed reclaim",
                    _ => "Not bound",
                });
            case Pvc:
                if (used.Count > 0) return (UsageStatus.Used, "");
                var ph = o.StringAt("status", "phase");
                return (UsageStatus.Unused, ph == "Bound" ? "Bound, but no pod or controller reads it. It still holds a volume (and its cost)." : $"Not mounted ({ph})");
            case StorageClass:
                if (used.Count > 0) return (UsageStatus.Used, o.Name == defaultClass ? "Default class" : "");
                return (UsageStatus.Unused, o.Name == defaultClass ? "Default class: unused today, but any claim without a class would get it" : "No claim or volume uses it");
            case Secret:
                if (used.Count > 0) return (UsageStatus.Used, "");
                var type = o.StringAt("type") ?? "";
                if (type == "helm.sh/release.v1") return (UsageStatus.External, "Helm release data");
                if (type == "kubernetes.io/service-account-token") return (UsageStatus.External, "Service account token (mounted by the token controller)");
                if (type.StartsWith("bootstrap.kubernetes.io/", StringComparison.Ordinal)) return (UsageStatus.External, "Bootstrap token");
                if (o.Annotations.TryGetValue("cert-manager.io/certificate-name", out var cert)) return (UsageStatus.External, $"Issued for cert-manager Certificate {cert}: read by what serves the certificate");
                if (Controller(o) is { } owner) return (UsageStatus.External, $"Owned by {owner}");
                return (UsageStatus.Unused, type == "kubernetes.io/tls" ? "TLS secret: no ingress or pod refers to it (a gateway or controller may still read it)" : "");
            default: // ConfigMap
                if (used.Count > 0) return (UsageStatus.Used, "");
                if (o.Name == "kube-root-ca.crt") return (UsageStatus.External, "Injected into every namespace by the root CA publisher");
                if (o.Annotations.ContainsKey("control-plane.alpha.kubernetes.io/leader")) return (UsageStatus.External, "Leader election lock");
                if (Controller(o) is { } own) return (UsageStatus.External, $"Owned by {own}");
                return (UsageStatus.Unused, "");
        }
    }

    private static string? Controller(KubeObject o) => o.OwnerReferences.FirstOrDefault(r => r.Controller) is { } r ? $"{r.Kind}/{r.Name}" : null;

    private static bool IsDefaultClass(KubeObject sc) =>
        sc.Annotations.TryGetValue("storageclass.kubernetes.io/is-default-class", out var v) && v == "true"
        || sc.Annotations.TryGetValue("storageclass.beta.kubernetes.io/is-default-class", out var b) && b == "true";

    // ---------- pod specs ----------

    /// <summary>Controllers (through their template) and the pods nothing controls: the pods a controller owns repeat its references.</summary>
    private static bool IsConsumer(KubeObject o) => o.Kind switch
    {
        "Pod" => !o.OwnerReferences.Any(r => r.Controller && ControllerKinds.Contains(r.Kind)),
        "ReplicaSet" or "Job" => !o.OwnerReferences.Any(r => r.Controller),
        "Deployment" or "StatefulSet" or "DaemonSet" or "CronJob" => true,
        _ => false,
    };

    private static JsonObject? PodSpecOf(KubeObject o) => o.Kind switch
    {
        "Pod" => o.At("spec") as JsonObject,
        "CronJob" => o.At("spec", "jobTemplate", "spec", "template", "spec") as JsonObject,
        _ => o.At("spec", "template", "spec") as JsonObject,
    };

    private static bool OptionalFlag(JsonNode? n) => n?["optional"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<(string kind, string name, string how, bool optional)> PodSpecRefs(JsonObject spec)
    {
        foreach (var v in spec["volumes"] as JsonArray ?? [])
        {
            if (v?["configMap"] is { } cm && cm["name"]?.ToString() is { Length: > 0 } cmn) yield return (ConfigMap, cmn, "volume", OptionalFlag(cm));
            if (v?["secret"] is { } s && s["secretName"]?.ToString() is { Length: > 0 } sn) yield return (Secret, sn, "volume", OptionalFlag(s));
            if (v?["persistentVolumeClaim"]?["claimName"]?.ToString() is { Length: > 0 } pvc) yield return (Pvc, pvc, "volume", false);
            if (v?["csi"]?["nodePublishSecretRef"]?["name"]?.ToString() is { Length: > 0 } csi) yield return (Secret, csi, "csi", false);
            foreach (var src in v?["projected"]?["sources"] as JsonArray ?? [])
            {
                if (src?["configMap"] is { } pcm && pcm["name"]?.ToString() is { Length: > 0 } pcmn) yield return (ConfigMap, pcmn, "projected volume", OptionalFlag(pcm));
                if (src?["secret"] is { } ps && ps["name"]?.ToString() is { Length: > 0 } psn) yield return (Secret, psn, "projected volume", OptionalFlag(ps));
            }
        }
        foreach (var s in spec["imagePullSecrets"] as JsonArray ?? [])
            if (s?["name"]?.ToString() is { Length: > 0 } n) yield return (Secret, n, "imagePullSecrets", false);
        foreach (var field in new[] { "initContainers", "containers", "ephemeralContainers" })
            foreach (var c in spec[field] as JsonArray ?? [])
            {
                foreach (var from in c?["envFrom"] as JsonArray ?? [])
                {
                    if (from?["configMapRef"] is { } cm && cm["name"]?.ToString() is { Length: > 0 } cmn) yield return (ConfigMap, cmn, "envFrom", OptionalFlag(cm));
                    if (from?["secretRef"] is { } s && s["name"]?.ToString() is { Length: > 0 } sn) yield return (Secret, sn, "envFrom", OptionalFlag(s));
                }
                foreach (var env in c?["env"] as JsonArray ?? [])
                {
                    if (env?["valueFrom"]?["configMapKeyRef"] is { } cm && cm["name"]?.ToString() is { Length: > 0 } cmn) yield return (ConfigMap, cmn, "env", OptionalFlag(cm));
                    if (env?["valueFrom"]?["secretKeyRef"] is { } s && s["name"]?.ToString() is { Length: > 0 } sn) yield return (Secret, sn, "env", OptionalFlag(s));
                }
            }
    }

    /// <summary>A StatefulSet's claims are named "{template}-{set}-{ordinal}" and carry no owner reference: they are read from the naming rule.</summary>
    private static void AddClaimTemplates(KubeObject sts, List<KubeObject> claims, List<Ref> refs)
    {
        foreach (var t in sts.At("spec", "volumeClaimTemplates") as JsonArray ?? [])
        {
            if (t?["metadata"]?["name"]?.ToString() is not { Length: > 0 } tn) continue;
            var pattern = new Regex($"^{Regex.Escape(tn)}-{Regex.Escape(sts.Name)}-\\d+$", RegexOptions.CultureInvariant);
            foreach (var pvc in claims)
                if (pvc.Namespace == sts.Namespace && pattern.IsMatch(pvc.Name))
                    refs.Add(new Ref(Pvc, pvc.Namespace, pvc.Name, new Consumer(sts.Kind, sts.Namespace, sts.Name, "volumeClaimTemplate"), false));
        }
    }
}
