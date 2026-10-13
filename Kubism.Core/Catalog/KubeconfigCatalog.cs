using Kubism.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Kubism.Core.Catalog;

public interface IKubeconfigCatalog : IDisposable
{
    IReadOnlyList<ClusterEntry> Entries { get; }
    IReadOnlyList<KubeconfigError> Errors { get; }
    ClusterEntry? Find(string id);
    void Reload();
    void AddPath(string path);
    /// <summary>Takes a cluster out of the catalog without touching any kubeconfig file: drops the added file or folder that provides it, or hides the context when it comes from KUBECONFIG / ~/.kube/config.</summary>
    RemoveResult Remove(ClusterEntry entry);
    /// <summary>Shows again the contexts hidden by <see cref="Remove"/>.</summary>
    void RestoreHidden();
    event Action? CatalogChanged;
}

public enum RemoveResult { RemovedFile, RemovedFolder, Hidden }

/// <summary>Builds the cluster list from KUBECONFIG, ~/.kube/config and user-added files/folders.</summary>
public sealed class KubeconfigCatalog : IKubeconfigCatalog
{
    private readonly ISettingsStore _settings;
    private readonly IClusterEventsService? _events;
    private readonly Func<string?> _kubeconfigEnv;
    private readonly string _homeConfig;
    private readonly TimeSpan _debounce;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private Timer? _timer;
    private IReadOnlyList<ClusterEntry> _entries = [];
    private IReadOnlyList<KubeconfigError> _errors = [];
    private HashSet<string> _watchedDirs = new(StringComparer.Ordinal);

    public KubeconfigCatalog(ISettingsStore settings, IClusterEventsService? events = null, Func<string?>? kubeconfigEnv = null,
        string? homeConfig = null, TimeSpan? debounce = null, bool watch = true)
    {
        _settings = settings;
        _events = events;
        _kubeconfigEnv = kubeconfigEnv ?? (() => Environment.GetEnvironmentVariable("KUBECONFIG"));
        _homeConfig = homeConfig ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _watch = watch;
        Reload();
    }

    private readonly bool _watch;

    public IReadOnlyList<ClusterEntry> Entries => _entries;
    public IReadOnlyList<KubeconfigError> Errors => _errors;
    public event Action? CatalogChanged;

    public ClusterEntry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    public static string MakeId(string file, string context) => $"{file}|{context}";

    public void AddPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!_settings.Current.ExtraKubeconfigPaths.Contains(path))
        {
            _settings.Current.ExtraKubeconfigPaths.Add(path);
            _settings.Save();
        }
        Reload();
    }

    public RemoveResult Remove(ClusterEntry entry)
    {
        var extras = _settings.Current.ExtraKubeconfigPaths;
        var result = RemoveResult.Hidden;
        if (extras.Remove(entry.FilePath)) result = RemoveResult.RemovedFile;
        else
        {
            var dir = Path.GetDirectoryName(entry.FilePath);
            var folder = extras.FirstOrDefault(x => Directory.Exists(x) && string.Equals(Path.GetFullPath(x), dir, StringComparison.Ordinal));
            if (folder is not null) { extras.Remove(folder); result = RemoveResult.RemovedFolder; }
        }
        // A file or folder that is also reachable through KUBECONFIG or the default path stays: hide what was asked.
        if (result == RemoveResult.Hidden || _entries.Any(e => e.Id == entry.Id) && IsImplicit(entry.FilePath))
        {
            if (!_settings.Current.HiddenClusterIds.Contains(entry.Id)) _settings.Current.HiddenClusterIds.Add(entry.Id);
            result = RemoveResult.Hidden;
        }
        _settings.Save();
        Reload();
        return result;
    }

    private bool IsImplicit(string file)
    {
        file = Path.GetFullPath(file);
        if (file == Path.GetFullPath(_homeConfig)) return true;
        var env = _kubeconfigEnv();
        return !string.IsNullOrEmpty(env) && env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(p => Path.GetFullPath(p) == file);
    }

    public void RestoreHidden()
    {
        if (_settings.Current.HiddenClusterIds.Count == 0) return;
        _settings.Current.HiddenClusterIds.Clear();
        _settings.Save();
        Reload();
    }

    public void Reload()
    {
        var files = new List<string>();
        void AddFile(string f)
        {
            f = Path.GetFullPath(f);
            if (!files.Contains(f)) files.Add(f);
        }

        var env = _kubeconfigEnv();
        if (!string.IsNullOrEmpty(env))
            foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) AddFile(p);
        AddFile(_homeConfig);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extra in _settings.Current.ExtraKubeconfigPaths)
        {
            if (Directory.Exists(extra))
            {
                dirs.Add(extra);
                foreach (var f in Directory.EnumerateFiles(extra).OrderBy(f => f, StringComparer.Ordinal))
                    if (!Path.GetFileName(f).StartsWith('.')) AddFile(f);
            }
            else AddFile(extra);
        }

        var entries = new List<ClusterEntry>();
        var errors = new List<KubeconfigError>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                // A missing default file is normal; a missing explicit one is worth reporting.
                if (file != Path.GetFullPath(_homeConfig)) errors.Add(new KubeconfigError(file, "File not found"));
                continue;
            }
            try { entries.AddRange(ParseFile(file).Where(e => !_settings.Current.HiddenClusterIds.Contains(e.Id))); }
            catch (Exception ex) { errors.Add(new KubeconfigError(file, ex.Message)); }
        }

        _entries = entries;
        _errors = errors;
        UpdateWatchers(files, dirs);
        CatalogChanged?.Invoke();
        _events?.Push(new Events.CatalogChanged());
    }

    /// <summary>Parses all contexts of a kubeconfig file.</summary>
    public static IReadOnlyList<ClusterEntry> ParseFile(string file)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(file))) yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Not a kubeconfig (empty document)");

        var clusters = NamedItems(root, "clusters");
        var result = new List<ClusterEntry>();
        if (root.Children.TryGetValue("contexts", out var ctxNode) && ctxNode is YamlSequenceNode seq)
        {
            foreach (var item in seq.OfType<YamlMappingNode>())
            {
                var name = Scalar(item, "name");
                if (name is null || Child(item, "context") is not { } ctx) continue;
                var clusterName = Scalar(ctx, "cluster") ?? "";
                var server = clusters.TryGetValue(clusterName, out var c) ? Scalar(Child(c, "cluster"), "server") ?? "" : "";
                result.Add(new ClusterEntry(MakeId(file, name), file, name, server, Scalar(ctx, "user") ?? "", Scalar(ctx, "namespace")));
            }
        }
        else if (!root.Children.ContainsKey("clusters") && !root.Children.ContainsKey("users"))
        {
            throw new InvalidDataException("Not a kubeconfig (no contexts)");
        }
        return result;
    }

    private static Dictionary<string, YamlMappingNode> NamedItems(YamlMappingNode root, string key)
    {
        var d = new Dictionary<string, YamlMappingNode>();
        if (root.Children.TryGetValue(key, out var n) && n is YamlSequenceNode s)
            foreach (var m in s.OfType<YamlMappingNode>())
                if (Scalar(m, "name") is { } name) d[name] = m;
        return d;
    }

    private static YamlMappingNode? Child(YamlMappingNode? n, string key) =>
        n is not null && n.Children.TryGetValue(key, out var c) ? c as YamlMappingNode : null;

    private static string? Scalar(YamlMappingNode? n, string key) =>
        n is not null && n.Children.TryGetValue(key, out var c) && c is YamlScalarNode s ? s.Value : null;

    private void UpdateWatchers(List<string> files, HashSet<string> dirs)
    {
        if (!_watch) return;
        var wanted = new HashSet<string>(dirs, StringComparer.Ordinal);
        foreach (var f in files) if (Path.GetDirectoryName(f) is { } d && Directory.Exists(d)) wanted.Add(d);
        lock (_gate)
        {
            if (wanted.SetEquals(_watchedDirs)) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var d in wanted)
            {
                try
                {
                    var w = new FileSystemWatcher(d) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
                    w.Changed += OnFsEvent; w.Created += OnFsEvent; w.Deleted += OnFsEvent; w.Renamed += OnFsEvent;
                    _watchers.Add(w);
                }
                catch (Exception ex) { AppLog.Warn($"Cannot watch {d}", ex); }
            }
            _watchedDirs = wanted;
        }
    }

    private HashSet<string> RelevantFiles() =>
        [.. _entries.Select(e => e.FilePath), .. _errors.Select(e => e.FilePath), Path.GetFullPath(_homeConfig)];

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // Compare by file name: macOS reports /private/var/... for /var/... paths (symlinks), so full paths can differ.
        var name = Path.GetFileName(e.FullPath);
        if (!RelevantFiles().Any(f => Path.GetFileName(f) == name) && !_settings.Current.ExtraKubeconfigPaths.Any(p => Directory.Exists(p))) return;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => { try { Reload(); } catch (Exception ex) { AppLog.Error("Catalog reload", ex); } }, null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
