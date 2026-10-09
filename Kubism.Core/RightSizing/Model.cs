namespace Kubism.Core.RightSizing;

public readonly record struct ContainerKey(string Namespace, string Pod, string Container);
public readonly record struct UsagePoint(DateTimeOffset Time, double CpuCores, double MemoryBytes);
public sealed record UsageSeries(ContainerKey Key, IReadOnlyList<UsagePoint> Points);
public sealed record ContainerExtras(double ThrottledRatio, int OomKills);
public sealed record VpaTarget(string Namespace, string WorkloadKind, string WorkloadName, string Container, double CpuTarget, double MemoryTarget,
    double CpuLower, double CpuUpper, double MemoryLower, double MemoryUpper);

public sealed class UsageHistory
{
    public string SourceName { get; init; } = "";
    public TimeSpan Window { get; init; }
    /// <summary>Fraction (0..1) of the window covered by samples.</summary>
    public double Coverage { get; init; }
    public double DataHours { get; init; }
    public IReadOnlyList<UsageSeries> Series { get; init; } = [];
    public IReadOnlyDictionary<ContainerKey, ContainerExtras> Extras { get; init; } = new Dictionary<ContainerKey, ContainerExtras>();
    public IReadOnlyList<VpaTarget> Vpa { get; init; } = [];
    public string? Note { get; init; }
}

public sealed record UsageQuery(IReadOnlyList<string>? Namespaces, TimeSpan Window, TimeSpan Step)
{
    public static UsageQuery Default(int days = 7, IReadOnlyList<string>? namespaces = null) =>
        new(namespaces, TimeSpan.FromDays(days), TimeSpan.FromMinutes(days <= 2 ? 1 : days <= 8 ? 5 : 15));
}

public interface IUsageHistorySource
{
    string Name { get; }
    Task<UsageHistory> QueryAsync(UsageQuery query, CancellationToken ct);
    /// <summary>Quick connectivity check for the "Test connection" button.</summary>
    Task<string> TestAsync(CancellationToken ct);
}

public enum Verdict { Ok, NoRequest, OverProvisioned, UnderProvisioned, LimitTooTight, NotEnoughData }

public sealed record RecommendedResources(double CpuRequest, double? CpuLimit, double MemoryRequest, double? MemoryLimit, string Strategy);

public sealed record ContainerReport
{
    public string Namespace { get; init; } = "";
    public string WorkloadKind { get; init; } = "";
    public string WorkloadName { get; init; } = "";
    public string Container { get; init; } = "";
    public int Replicas { get; init; }
    public string? Qos { get; init; }
    public double? CpuRequest { get; init; }
    public double? CpuLimit { get; init; }
    public double? MemoryRequest { get; init; }
    public double? MemoryLimit { get; init; }
    public double CpuP50 { get; init; }
    public double CpuP95 { get; init; }
    public double CpuP99 { get; init; }
    public double CpuMax { get; init; }
    public double ThrottledRatio { get; init; }
    public double MemoryP95 { get; init; }
    public double MemoryMax { get; init; }
    public int OomKills { get; init; }
    public int Restarts { get; init; }
    public double Coverage { get; init; }
    public double DataHours { get; init; }
    public Verdict CpuVerdict { get; init; }
    public Verdict MemoryVerdict { get; init; }
    public RecommendedResources? Recommended { get; init; }
    public double WasteCpuCores { get; init; }
    public double WasteMemoryBytes { get; init; }
    public double? WasteCostPerHour { get; init; }
    public string? GitOpsManager { get; init; }

    public Verdict Primary => Worst(CpuVerdict, MemoryVerdict);

    public static Verdict Worst(Verdict a, Verdict b)
    {
        static int Rank(Verdict v) => v switch
        {
            Verdict.UnderProvisioned => 5, Verdict.LimitTooTight => 4, Verdict.NoRequest => 3, Verdict.OverProvisioned => 2, Verdict.NotEnoughData => 1, _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    public string Id => $"{Namespace}/{WorkloadKind}/{WorkloadName}/{Container}";
}

public sealed record WorkloadReport(string Namespace, string Kind, string Name, int Replicas, IReadOnlyList<ContainerReport> Containers)
{
    public double WasteCpuCores => Containers.Sum(c => c.WasteCpuCores);
    public double WasteMemoryBytes => Containers.Sum(c => c.WasteMemoryBytes);
    public Verdict Primary => Containers.Aggregate(Verdict.Ok, (v, c) => ContainerReport.Worst(v, c.Primary));
    public string? GitOpsManager => Containers.Select(c => c.GitOpsManager).FirstOrDefault(m => m is not null);
}

public sealed record NamespaceRollup(string Namespace, double CpuRequested, double CpuUsedP95, double MemoryRequested, double MemoryUsedP95, double WasteCpu, double WasteMemory);

public sealed record RightSizingReport(
    string Source, TimeSpan Window, double Coverage, double DataHours, string Strategy,
    IReadOnlyList<WorkloadReport> Workloads, IReadOnlyList<NamespaceRollup> Namespaces,
    double CpuRequested, double CpuUsed, double MemoryRequested, double MemoryUsed, double WasteCpu, double WasteMemory,
    double? WasteCostPerMonth, int UnderProvisioned, int WithoutRequests, double NodeCpuAllocatable, double NodeMemoryAllocatable,
    double CpuLimits = 0, double MemoryLimits = 0)
{
    public IEnumerable<ContainerReport> Containers => Workloads.SelectMany(w => w.Containers);
}
