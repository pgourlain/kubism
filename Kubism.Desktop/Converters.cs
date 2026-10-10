using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kubism.Core.Sessions;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop;

public static class Converters
{
    public static readonly IValueConverter HexBrush = new FuncConverter<string?, IBrush>(s =>
        Color.TryParse(s ?? "", out var c) ? new SolidColorBrush(c) : new SolidColorBrush(Color.Parse("#3B82F6")));

    public static readonly IValueConverter SeverityBrush = new FuncConverter<Severity, IBrush?>(s => Brush(s switch
    {
        Severity.Ok => "KubismOkBrush", Severity.Warning => "KubismWarnBrush", Severity.Error => "KubismErrorBrush", _ => "KubismMutedBrush",
    }));

    public static readonly IValueConverter StateBrush = new FuncConverter<SessionState, IBrush?>(s => Brush(s switch
    {
        SessionState.Connected => "KubismOkBrush", SessionState.Degraded => "KubismWarnBrush", SessionState.Connecting => "KubismAccentBrush",
        SessionState.Error => "KubismErrorBrush", _ => "KubismMutedBrush",
    }));

    public static readonly IValueConverter SafetyBrush = new FuncConverter<bool, IBrush?>(isProd => Brush(isProd ? "KubismProductionBrush" : "KubismReadOnlyBrush"));
    public static readonly IValueConverter BoolToOpacityDim = new FuncConverter<bool, double>(dim => dim ? 0.45 : 1.0);
    public static readonly IValueConverter NotNull = new FuncConverter<object?, bool>(o => o is not null);
    public static readonly IValueConverter IsNull = new FuncConverter<object?, bool>(o => o is null);
    public static readonly IValueConverter Bytes = new FuncConverter<double, string>(d => Core.Views.Quantities.FormatBytes(d));

    public static IBrush? Brush(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.TryGetResource(key, app.ActualThemeVariant, out var v) ? v as IBrush : null;
    }

    public static readonly IValueConverter GroupWeight = new FuncConverter<bool, FontWeight>(g => g ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter RevealLabel = new FuncConverter<bool, string>(r => r ? "Hide" : "Reveal");

    public static readonly IValueConverter OkMark = new FuncConverter<bool, string>(r => r ? "✔" : "✘");

    public static readonly IValueConverter SplitColumn = new FuncConverter<bool, GridLength>(split => split ? new GridLength(1, GridUnitType.Star) : new GridLength(0));

    public static readonly IValueConverter SidebarColumn = new FuncConverter<bool, GridLength>(compact => compact ? new GridLength(0) : new GridLength(230));

    public static readonly IValueConverter HelmHealth = new FuncConverter<Core.Helm.HelmRelease, string>(r => Core.Helm.HelmDiagnostics.IsProblem(r, DateTimeOffset.UtcNow) ? "⚠ " + r.Status : r.Status);

    public static readonly IValueConverter ProblemBrush = new FuncConverter<bool, IBrush?>(p => Brush(p ? "KubismErrorBrush" : "KubismOkBrush"));
    public static readonly IValueConverter PreviousLabel = new FuncConverter<bool, string>(p => p ? "  (previous, crashed run)" : "  (current)");

    public static readonly IValueConverter Icon = new FuncConverter<string?, Geometry?>(k => k is not null && Icons.Paths.TryGetValue(k, out var p) ? Geometry.Parse(p) : null);

    private sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> f) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is TIn v ? f(v) : value is null && default(TIn) is null ? f(default!) : default(TOut);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public static class Icons
{
    // One vector set (24×24 grid, stroke style paths) for kinds and actions.
    public static readonly Dictionary<string, string> Paths = new()
    {
        // Stroke icons (24×24 grid): drawn as outlines, never filled.
        ["overview"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
        ["rightsizing"] = "M5,20 V11 M12,20 V4 M19,20 V14",
        ["node"] = "M4,5 H20 V11 H4 Z M4,13 H20 V19 H4 Z M8,8 H8.5 M8,16 H8.5",
        ["namespaces"] = "M3,7 L12,3 L21,7 L12,11 Z M3,12 L12,16 L21,12 M3,17 L12,21 L21,17",
        ["events"] = "M6,16 V11 A6,6 0 0 1 18,11 V16 L20,18 H4 Z M10,21 H14",
        ["helm"] = "M12,3 V21 M3,12 H21 M5.6,5.6 L18.4,18.4 M18.4,5.6 L5.6,18.4 M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
        ["create"] = "M12,4 V20 M4,12 H20",
        ["activity"] = "M2,12 H6 L9,4 L15,20 L18,12 H22",
        ["portforward"] = "M3,12 H17 M13,7 L18,12 L13,17 M21,5 V19",
        ["pod"] = "M12,2 L21,7 V17 L12,22 L3,17 V7 Z",
        ["logs"] = "M5,4 H19 V20 H5 Z M8,8 H16 M8,12 H16 M8,16 H13",
        ["shell"] = "M3,5 H21 V19 H3 Z M7,9 L10,12 L7,15 M12,15 H16",
        ["delete"] = "M5,7 H19 M9,7 V4 H15 V7 M7,7 L8,20 H16 L17,7",
        ["edit"] = "M4,20 L5,15 L16,4 L20,8 L9,19 Z",
        ["refresh"] = "M20,12 A8,8 0 1 1 16,5 M20,3 V9 H14",
    };
}
