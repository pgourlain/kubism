using System.Globalization;
using System.Text;
using Kubism.Core.Metrics;

namespace Kubism.Core.RightSizing;

public readonly record struct StoredSample(DateTimeOffset Time, string Namespace, string Pod, string Container, double Cpu, double Memory);

public interface IUsageStore
{
    void Append(string clusterId, IReadOnlyList<StoredSample> samples);
    IReadOnlyList<StoredSample> Read(string clusterId, DateTimeOffset from, DateTimeOffset to);
    /// <summary>Drops data older than the retention and downsamples data older than 24h to 5-minute buckets.</summary>
    void Compact(string clusterId, DateTimeOffset now, TimeSpan retention);
    long SizeBytes { get; }
}

/// <summary>Append-only text files, one per cluster and day, in the app-data folder (no native dependency, AOT-friendly).</summary>
public sealed class FileUsageStore(string folder) : IUsageStore
{
    private readonly object _gate = new();

    private string Dir(string clusterId)
    {
        var safe = string.Concat(clusterId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        if (safe.Length > 80) safe = safe[^80..] + (uint)clusterId.GetHashCode();
        return Path.Combine(folder, safe);
    }

    private static string FileName(DateTimeOffset t) => $"{t.UtcDateTime:yyyy-MM-dd}.log";

    public long SizeBytes
    {
        get
        {
            try { return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
            catch { return 0; }
        }
    }

    public void Append(string clusterId, IReadOnlyList<StoredSample> samples)
    {
        if (samples.Count == 0) return;
        lock (_gate)
        {
            var dir = Dir(clusterId);
            Directory.CreateDirectory(dir);
            foreach (var day in samples.GroupBy(s => FileName(s.Time)))
            {
                var sb = new StringBuilder();
                foreach (var s in day)
                    sb.Append(s.Time.ToUnixTimeSeconds()).Append('\t').Append(s.Namespace).Append('\t').Append(s.Pod).Append('\t').Append(s.Container).Append('\t')
                      .Append(s.Cpu.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(s.Memory.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                File.AppendAllText(Path.Combine(dir, day.Key), sb.ToString());
            }
        }
    }

    public IReadOnlyList<StoredSample> Read(string clusterId, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<StoredSample>();
        lock (_gate)
        {
            var dir = Dir(clusterId);
            if (!Directory.Exists(dir)) return result;
            for (var d = from.UtcDateTime.Date; d <= to.UtcDateTime.Date; d = d.AddDays(1))
            {
                var f = Path.Combine(dir, $"{d:yyyy-MM-dd}.log");
                if (!File.Exists(f)) continue;
                foreach (var line in File.ReadLines(f))
                {
                    var p = line.Split('\t');
                    if (p.Length != 6 || !long.TryParse(p[0], out var ts)) continue;
                    var t = DateTimeOffset.FromUnixTimeSeconds(ts);
                    if (t < from || t > to) continue;
                    result.Add(new StoredSample(t, p[1], p[2], p[3], double.Parse(p[4], CultureInfo.InvariantCulture), double.Parse(p[5], CultureInfo.InvariantCulture)));
                }
            }
        }
        return result;
    }

    public void Compact(string clusterId, DateTimeOffset now, TimeSpan retention)
    {
        lock (_gate)
        {
            var dir = Dir(clusterId);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.log"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)) continue;
                if (now.UtcDateTime - day > retention) { File.Delete(f); continue; }
                if (now.UtcDateTime - day.AddDays(1) < TimeSpan.FromHours(24)) continue;
                var marker = f + ".done";
                if (File.Exists(marker)) continue;
                var buckets = new Dictionary<(long, string, string, string), (double cpuSum, double memMax, int n)>();
                foreach (var line in File.ReadLines(f))
                {
                    var p = line.Split('\t');
                    if (p.Length != 6 || !long.TryParse(p[0], out var ts)) continue;
                    var key = (ts / 300 * 300, p[1], p[2], p[3]);
                    var cpu = double.Parse(p[4], CultureInfo.InvariantCulture); var mem = double.Parse(p[5], CultureInfo.InvariantCulture);
                    buckets.TryGetValue(key, out var b);
                    buckets[key] = (b.cpuSum + cpu, Math.Max(b.memMax, mem), b.n + 1);
                }
                var sb = new StringBuilder();
                foreach (var (k, v) in buckets.OrderBy(k => k.Key.Item1))
                    sb.Append(k.Item1).Append('\t').Append(k.Item2).Append('\t').Append(k.Item3).Append('\t').Append(k.Item4).Append('\t')
                      .Append((v.cpuSum / v.n).ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(v.memMax.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(f, sb.ToString());
                File.WriteAllText(marker, "");
            }
        }
    }
}

/// <summary>Samples metrics-server every 30s while enabled for a cluster, writing to the local store; gaps (app closed, disconnected) are visible as missing buckets.</summary>
public sealed class LocalSampler : IAsyncDisposable
{
    private readonly string _clusterId;
    private readonly MetricsService _metrics;
    private readonly IUsageStore _store;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LocalSampler(string clusterId, MetricsService metrics, IUsageStore store, TimeSpan? interval = null)
    {
        _clusterId = clusterId; _metrics = metrics; _store = store; Interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Interval { get; }
    public int SamplesWritten { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var lease = _metrics.Acquire();
            var lastCompact = DateTimeOffset.MinValue;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await SampleOnceAsync(ct).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow - lastCompact > TimeSpan.FromHours(6)) { _store.Compact(_clusterId, DateTimeOffset.UtcNow, TimeSpan.FromDays(30)); lastCompact = DateTimeOffset.UtcNow; }
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { lease.Dispose(); }
        });
    }

    public async Task SampleOnceAsync(CancellationToken ct)
    {
        await _metrics.PollOnceAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var samples = _metrics.Pods.Values.SelectMany(p => p.Containers.Select(c => new StoredSample(now, p.Namespace, p.Name, c.Name, c.CpuCores, c.MemoryBytes))).ToList();
        _store.Append(_clusterId, samples);
        SamplesWritten += samples.Count;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
    }
}

public sealed class LocalSamplerSource(string clusterId, IUsageStore store) : IUsageHistorySource
{
    public string Name => "Local sampling";
    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult($"Local store: {store.SizeBytes / 1024} KiB");

    public Task<UsageHistory> QueryAsync(UsageQuery q, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow; var start = end - q.Window;
        var samples = store.Read(clusterId, start, end)
            .Where(s => q.Namespaces is not { Count: > 0 } || q.Namespaces.Contains(s.Namespace)).ToList();
        var series = samples.GroupBy(s => new ContainerKey(s.Namespace, s.Pod, s.Container))
            .Select(g => new UsageSeries(g.Key, g.OrderBy(s => s.Time).Select(s => new UsagePoint(s.Time, s.Cpu, s.Memory)).ToList())).ToList();
        // Coverage = share of 5-minute buckets of the window that hold at least one sample (gaps while the app was closed are real gaps).
        var buckets = samples.Select(s => s.Time.ToUnixTimeSeconds() / 300).Distinct().Count();
        var expected = Math.Max(1, q.Window.TotalSeconds / 300);
        var first = samples.Count == 0 ? end : samples.Min(s => s.Time);
        return Task.FromResult(new UsageHistory
        {
            SourceName = Name, Window = q.Window, Series = series, Coverage = Math.Min(1, buckets / expected), DataHours = (end - first).TotalHours,
            Note = $"{buckets * 5 / 60.0:0.#} hours of samples in the window",
        });
    }
}
