using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Catalog;

namespace Kubism.Desktop.ViewModels;

public sealed partial class SettingsEditorViewModel : ViewModelBase
{
    public SettingsEditorViewModel(AppSettings s)
    {
        Theme = s.Theme; Language = s.Language; LogBufferLines = s.LogBufferLines; MaxInformers = s.MaxInformersPerCluster;
        CheckForUpdates = s.CheckForUpdates; DimForbiddenKinds = s.DimForbiddenKinds; ProductionRegex = s.ProductionRegex;
        CpuPercentile = s.CpuPercentile; MemoryMargin = s.MemoryMargin; MemoryLimitFactor = s.MemoryLimitFactor; KeepCpuLimit = s.KeepCpuLimit;
    }

    public string[] Themes { get; } = ["System", "Light", "Dark"];
    public string[] Languages { get; } = ["en", "fr"];
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _language;
    [ObservableProperty] private int _logBufferLines;
    [ObservableProperty] private int _maxInformers;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _dimForbiddenKinds;
    [ObservableProperty] private string _productionRegex;
    [ObservableProperty] private double _cpuPercentile;
    [ObservableProperty] private double _memoryMargin;
    [ObservableProperty] private double _memoryLimitFactor;
    [ObservableProperty] private bool _keepCpuLimit;

    public void ApplyTo(AppSettings s)
    {
        s.Theme = Theme; s.Language = Language; s.LogBufferLines = Math.Max(1000, LogBufferLines); s.MaxInformersPerCluster = Math.Max(0, MaxInformers);
        s.CheckForUpdates = CheckForUpdates; s.DimForbiddenKinds = DimForbiddenKinds;
        try { _ = new System.Text.RegularExpressions.Regex(ProductionRegex); s.ProductionRegex = ProductionRegex; } catch (ArgumentException) { }
        s.CpuPercentile = Math.Clamp(CpuPercentile, 50, 100); s.MemoryMargin = Math.Clamp(MemoryMargin, 1, 3); s.MemoryLimitFactor = Math.Clamp(MemoryLimitFactor, 1, 3); s.KeepCpuLimit = KeepCpuLimit;
    }
}
