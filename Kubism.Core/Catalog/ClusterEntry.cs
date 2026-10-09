namespace Kubism.Core.Catalog;

public enum SafetyMode { Normal, Production, ReadOnly }

/// <summary>One context of one kubeconfig file. Id = file path + context name.</summary>
public sealed record ClusterEntry(
    string Id,
    string FilePath,
    string ContextName,
    string Server,
    string User,
    string? DefaultNamespace)
{
    public string ServerHost
    {
        get
        {
            try { return new Uri(Server).Authority; } catch { return Server; }
        }
    }
    public string? Error { get; init; }
}

/// <summary>User metadata for a cluster, stored in settings and never written to kubeconfig files.</summary>
public sealed class ClusterMetadata
{
    public string? Label { get; set; }
    public string? Color { get; set; }
    public string? IconLetter { get; set; }
    public List<string> Tags { get; set; } = [];
    public SafetyMode SafetyMode { get; set; } = SafetyMode.Normal;
    public string? DefaultNamespace { get; set; }
    public List<string> Namespaces { get; set; } = [];
    public string? PrometheusUrl { get; set; }
    public bool LocalSampling { get; set; }
    public double? PricePerVcpuHour { get; set; }
    public double? PricePerGibHour { get; set; }
    public bool Pinned { get; set; }
}

public sealed record KubeconfigError(string FilePath, string Message);
