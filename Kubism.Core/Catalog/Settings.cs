using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kubism.Core.Catalog;

public sealed class AppSettings
{
    public List<string> ExtraKubeconfigPaths { get; set; } = [];
    public List<string> HiddenClusterIds { get; set; } = [];
    public Dictionary<string, ClusterMetadata> Clusters { get; set; } = [];
    public Dictionary<string, List<string>> SelectedNamespaces { get; set; } = [];
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = [];
    public string ProductionRegex { get; set; } = "prod|prd|production";
    public int LogBufferLines { get; set; } = 50_000;
    /// <summary>Maximum live informers per connected cluster (0 = unlimited). Keeps many connected clusters cheap.</summary>
    public int MaxInformersPerCluster { get; set; } = 60;
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "en";
    public bool DimForbiddenKinds { get; set; } = true;
    public bool EnableNodeShell { get; set; }
    public bool CheckForUpdates { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    // Right-sizing strategy
    public double CpuPercentile { get; set; } = 95;
    public double MemoryMargin { get; set; } = 1.15;
    public double MemoryLimitFactor { get; set; } = 1.0;
    public bool KeepCpuLimit { get; set; }
    public int RightSizingWindowDays { get; set; } = 7;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ClusterMetadata))]
public partial class CoreJson : JsonSerializerContext;

public interface ISettingsStore
{
    AppSettings Current { get; }
    ClusterMetadata GetMetadata(string clusterId);
    void SetMetadata(string clusterId, ClusterMetadata metadata);
    void Save();
    event Action? Changed;
}

public sealed class SettingsStore : ISettingsStore
{
    private readonly string? _path;
    private readonly object _gate = new();

    /// <summary>In-memory when <paramref name="path"/> is null.</summary>
    public SettingsStore(string? path = null)
    {
        _path = path;
        Current = Load(path);
    }

    public AppSettings Current { get; }
    public event Action? Changed;

    public static string DefaultPath => Path.Combine(AppPaths.DataFolder, "settings.json");

    private static AppSettings Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.AppSettings) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public ClusterMetadata GetMetadata(string clusterId)
    {
        lock (_gate)
        {
            if (!Current.Clusters.TryGetValue(clusterId, out var m))
            {
                m = new ClusterMetadata();
                // Contexts whose name looks like production start as production.
                var ctx = clusterId.Contains('|') ? clusterId[(clusterId.LastIndexOf('|') + 1)..] : clusterId;
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(ctx, Current.ProductionRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        m.SafetyMode = SafetyMode.Production;
                        m.Tags.Add("production");
                    }
                }
                catch (ArgumentException) { /* bad user regex */ }
            }
            return m;
        }
    }

    public void SetMetadata(string clusterId, ClusterMetadata metadata)
    {
        lock (_gate) Current.Clusters[clusterId] = metadata;
        Save();
    }

    public void Save()
    {
        if (_path is not null)
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, CoreJson.Default.AppSettings));
                File.Move(tmp, _path, true);
            }
        }
        Changed?.Invoke();
    }
}

public static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("KUBISM_DATA");
            if (!string.IsNullOrEmpty(over)) return over;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kubism");
        }
    }
}
