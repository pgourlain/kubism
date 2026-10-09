using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Catalog;
using Kubism.Core.Sessions;
using Kubism.Desktop.Services;

namespace Kubism.Desktop.ViewModels;

/// <summary>One context in the catalog (left column).</summary>
public sealed partial class ClusterItemViewModel : ViewModelBase
{
    private readonly ISettingsStore _settings;

    public ClusterItemViewModel(ClusterEntry entry, ISettingsStore settings)
    {
        Entry = entry; _settings = settings;
    }

    public ClusterEntry Entry { get; }
    public string Id => Entry.Id;
    public ClusterMetadata Metadata => _settings.GetMetadata(Entry.Id);
    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.Label) ? Entry.ContextName : Metadata.Label!;
    public string Host => Entry.ServerHost;
    public string Letter => string.IsNullOrWhiteSpace(Metadata.IconLetter) ? (DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?") : Metadata.IconLetter!;
    public string Color => Metadata.Color ?? DefaultColor(Entry.Id);
    public SafetyMode SafetyMode => Metadata.SafetyMode;
    public bool IsProduction => SafetyMode == SafetyMode.Production;
    public bool IsReadOnly => SafetyMode == SafetyMode.ReadOnly;
    public string SafetyLabel => SafetyMode switch { SafetyMode.Production => "Production".T(), SafetyMode.ReadOnly => "Read-only".T(), _ => "" };
    public string FilePath => Entry.FilePath;

    [ObservableProperty] private SessionState _state = SessionState.Disconnected;
    [ObservableProperty] private string? _errorMessage;

    public bool IsConnected => State is SessionState.Connected or SessionState.Degraded;
    public bool IsConnecting => State == SessionState.Connecting;
    public bool HasError => State == SessionState.Error;
    public string StatusText => State switch
    {
        SessionState.Connected => "Connected",
        SessionState.Connecting => "Connecting…",
        SessionState.Degraded => "Degraded",
        SessionState.Error => ErrorMessage ?? "Error",
        _ => "",
    };

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsConnecting)); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(StatusText));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>Called after the metadata changed (editor dialog).</summary>
    public void RefreshMetadata()
    {
        foreach (var p in new[] { nameof(DisplayName), nameof(Letter), nameof(Color), nameof(SafetyMode), nameof(IsProduction), nameof(IsReadOnly), nameof(SafetyLabel) })
            OnPropertyChanged(p);
    }

    private static readonly string[] Palette = ["#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899", "#14B8A6", "#F97316", "#6366F1"];

    private static string DefaultColor(string id)
    {
        var h = 17;
        foreach (var c in id) h = unchecked(h * 31 + c);
        return Palette[(int)((uint)h % Palette.Length)];
    }
}
