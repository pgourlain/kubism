using Kubism.Core.Api;

namespace Kubism.Core.Images;

/// <summary>The inventory plus what could not be read, so a caller can say the list may be incomplete.</summary>
public sealed record ImageSnapshot(IReadOnlyList<ImageRow> Rows, IReadOnlyList<string> NotRead, IReadOnlyList<string> Truncated)
{
    public bool IsComplete => NotRead.Count == 0 && Truncated.Count == 0;

    public string? Warning => IsComplete ? null
        : $"{(NotRead.Count > 0 ? $"Could not read {string.Join(", ", NotRead)}. " : "")}{(Truncated.Count > 0 ? $"{string.Join(", ", Truncated)} truncated at {ImageLoader.ListCap}. " : "")}The list may be incomplete.";
}

/// <summary>One-shot read for callers with no live cache (the MCP server, the cluster comparison): lists pods, and replica sets and jobs to resolve owners.</summary>
public static class ImageLoader
{
    public const int ListCap = 5000;

    /// <param name="namespaces">Namespaces to read; <c>null</c> or empty (or a <c>null</c> entry) reads all of them.</param>
    public static async Task<ImageSnapshot> LoadAsync(IKubeTransport transport, IReadOnlyList<string?>? namespaces, CancellationToken ct)
    {
        IReadOnlyList<string?> scope = namespaces is { Count: > 0 } && !namespaces.Contains(null) ? namespaces : [null];
        var notRead = new List<string>();
        var truncated = new List<string>();

        async Task<List<KubeObject>> ReadAsync(Gvr gvr, string kind, bool required)
        {
            var items = new List<KubeObject>();
            try
            {
                foreach (var ns in scope)
                {
                    string? cont = null;
                    do
                    {
                        var page = await transport.ListAsync(gvr, ns, new ListOptions { Limit = 500, Continue = cont }, ct).ConfigureAwait(false);
                        foreach (var item in page.Items)
                        {
                            var o = new KubeObject(item);
                            if (o.Kind.Length == 0) item["kind"] = kind;
                            items.Add(o);
                        }
                        cont = string.IsNullOrEmpty(page.Continue) ? null : page.Continue;
                    } while (cont is not null && items.Count < ListCap);
                }
            }
            // Replica sets and jobs only resolve the owner of a pod: failing to read them is not worth a warning.
            catch (Exception ex) when (ex is KubeApiException or KubeConnectionException)
            {
                if (required) notRead.Add(gvr.Resource);
                return [];
            }
            if (items.Count >= ListCap) truncated.Add(gvr.Resource);
            return items;
        }

        var pods = ReadAsync(Gvr.Pods, "Pod", true);
        var sets = ReadAsync(Gvr.ReplicaSets, "ReplicaSet", false);
        var jobs = ReadAsync(Gvr.Jobs, "Job", false);
        await Task.WhenAll(pods, sets, jobs).ConfigureAwait(false);
        return new ImageSnapshot(ImageInventory.Analyze(pods.Result, sets.Result, jobs.Result), notRead, truncated);
    }
}
