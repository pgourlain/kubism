using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Kubism.Desktop.Controls;

/// <summary>AvaloniaEdit with a TextMate YAML grammar; read-only view or edit mode. Text is a two-way styled property.</summary>
public sealed class YamlEditor : UserControl
{
    private readonly TextEditor _editor = new()
    {
        ShowLineNumbers = true, FontFamily = new FontFamily("Menlo,Consolas,DejaVu Sans Mono,monospace"), FontSize = 12,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };
    private TextMate.Installation? _tm;
    private RegistryOptions? _registry;
    private bool _updating;

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<YamlEditor, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<YamlEditor, bool>(nameof(IsReadOnly), true);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public TextEditor Editor => _editor;

    public YamlEditor()
    {
        Content = _editor;
        _editor.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true; Text = _editor.Text; _updating = false;
        };
        AccessibilityName();
    }

    private void AccessibilityName() => Avalonia.Automation.AutomationProperties.SetName(this, "YAML editor");

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InstallGrammar();
    }

    private void InstallGrammar()
    {
        try
        {
            var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            _registry = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            _tm?.Dispose();
            _tm = _editor.InstallTextMate(_registry);
            _tm.SetGrammar(_registry.GetScopeByExtension(".yaml"));
        }
        catch (Exception ex) { Kubism.Core.AppLog.Warn($"TextMate unavailable", ex); }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_updating)
        {
            _updating = true;
            if (_editor.Text != (Text ?? "")) _editor.Text = Text ?? "";
            _updating = false;
        }
        else if (change.Property == IsReadOnlyProperty) _editor.IsReadOnly = IsReadOnly;
    }
}
