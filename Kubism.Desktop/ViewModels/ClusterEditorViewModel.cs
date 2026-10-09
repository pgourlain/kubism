using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Catalog;

namespace Kubism.Desktop.ViewModels;

/// <summary>Per-cluster metadata editor. Values are stored in settings, never written to kubeconfig files.</summary>
public sealed partial class ClusterEditorViewModel : ViewModelBase
{
    public ClusterEditorViewModel(string contextName, ClusterMetadata m)
    {
        ContextName = contextName;
        Label = m.Label ?? ""; Color = m.Color ?? ""; IconLetter = m.IconLetter ?? ""; Tags = string.Join(", ", m.Tags);
        Safety = m.SafetyMode; DefaultNamespace = m.DefaultNamespace ?? ""; PrometheusUrl = m.PrometheusUrl ?? "";
        LocalSampling = m.LocalSampling; PriceCpu = m.PricePerVcpuHour?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        PriceMem = m.PricePerGibHour?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        Namespaces = string.Join(", ", m.Namespaces);
    }

    public string ContextName { get; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _color;
    [ObservableProperty] private string _iconLetter;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private SafetyMode _safety;
    [ObservableProperty] private string _defaultNamespace;
    [ObservableProperty] private string _namespaces;
    [ObservableProperty] private string _prometheusUrl;
    [ObservableProperty] private bool _localSampling;
    [ObservableProperty] private string _priceCpu;
    [ObservableProperty] private string _priceMem;
    public SafetyMode[] SafetyModes { get; } = [SafetyMode.Normal, SafetyMode.Production, SafetyMode.ReadOnly];
    public string[] ColorChoices { get; } = ["", "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899", "#14B8A6", "#F97316", "#6366F1", "#EF4444"];

    public ClusterMetadata ToMetadata(ClusterMetadata original)
    {
        static double? D(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        return new ClusterMetadata
        {
            Label = Label.Trim().Length == 0 ? null : Label.Trim(), Color = Color.Trim().Length == 0 ? null : Color.Trim(),
            IconLetter = IconLetter.Trim().Length == 0 ? null : IconLetter.Trim()[..1],
            Tags = Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            SafetyMode = Safety, DefaultNamespace = DefaultNamespace.Trim().Length == 0 ? null : DefaultNamespace.Trim(),
            Namespaces = Namespaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            PrometheusUrl = PrometheusUrl.Trim().Length == 0 ? null : PrometheusUrl.Trim(), LocalSampling = LocalSampling,
            PricePerVcpuHour = D(PriceCpu), PricePerGibHour = D(PriceMem), Pinned = original.Pinned,
        };
    }
}
