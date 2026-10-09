namespace Kubism.Core.RightSizing;

public sealed record RightSizingSettings
{
    public double CpuPercentile { get; init; } = 95;
    public double MemoryMargin { get; init; } = 1.15;
    public double MemoryLimitFactor { get; init; } = 1.0;
    public bool KeepCpuLimit { get; init; }
    public double MinCpuCores { get; init; } = 0.010;
    public double MinMemoryBytes { get; init; } = 32d * 1024 * 1024;
    public double OverProvisionedRatio { get; init; } = 0.40;
    public double ThrottleThreshold { get; init; } = 0.10;
    public double TightLimitRatio { get; init; } = 0.90;
    public double MinCoverage { get; init; } = 0.50;
    public double MinDataHours { get; init; } = 24;
    public double? PricePerVcpuHour { get; init; }
    public double? PricePerGibHour { get; init; }
}

public interface IRightSizingStrategy
{
    string Name { get; }
    RecommendedResources Recommend(ContainerReport usage, double cpuAtPercentile, RightSizingSettings settings);
}

/// <summary>Mirrors Robusta KRR's simple strategy: CPU request = p95, no CPU limit; memory request = max × margin, memory limit = request.</summary>
public sealed class KrrStrategy : IRightSizingStrategy
{
    public string Name => "krr-simple (cpu p95, memory max × margin)";

    public RecommendedResources Recommend(ContainerReport u, double cpuAtPercentile, RightSizingSettings s)
    {
        var cpu = Rounding.Cpu(Math.Max(cpuAtPercentile, s.MinCpuCores));
        var mem = Rounding.Memory(Math.Max(u.MemoryMax * s.MemoryMargin, s.MinMemoryBytes));
        double? cpuLimit = s.KeepCpuLimit ? u.CpuLimit : null;
        if (cpuLimit is { } l && l < cpu) cpuLimit = cpu;
        var memLimit = Rounding.Memory(mem * s.MemoryLimitFactor);
        return new RecommendedResources(cpu, cpuLimit, mem, memLimit, Name);
    }
}

public static class Rounding
{
    /// <summary>Round CPU up to sensible steps: 5m below 100m, 10m below 500m, 50m above.</summary>
    public static double Cpu(double cores)
    {
        var m = cores * 1000;
        var step = m < 100 ? 5 : m < 500 ? 10 : 50;
        return Math.Ceiling(m / step - 1e-9) * step / 1000;
    }

    /// <summary>Round memory up to Mi steps: 8Mi below 512Mi, 32Mi below 4Gi, 128Mi above.</summary>
    public static double Memory(double bytes)
    {
        const double Mi = 1024 * 1024;
        var mi = bytes / Mi;
        var step = mi < 512 ? 8 : mi < 4096 ? 32 : 128;
        return Math.Ceiling(mi / step - 1e-9) * step * Mi;
    }
}
