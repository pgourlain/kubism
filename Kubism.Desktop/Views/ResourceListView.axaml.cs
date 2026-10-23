using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Kubism.Core.Table;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

/// <summary>Text cell that follows a row's Cells array (and severity for status columns) without reflection bindings.</summary>
internal sealed class CellTextBlock : TextBlock
{
    private ResourceRowViewModel? _row;
    private readonly int _index;
    private readonly bool _colored;

    public CellTextBlock(int index, bool colored)
    {
        _index = index; _colored = colored;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(8, 0);
        TextTrimming = TextTrimming.CharacterEllipsis;
        DataContextChanged += (_, _) => Attach(DataContext as ResourceRowViewModel);
    }

    private void Attach(ResourceRowViewModel? row)
    {
        if (_row is not null) _row.PropertyChanged -= OnChanged;
        _row = row;
        if (_row is not null) _row.PropertyChanged += OnChanged;
        Update();
    }

    private void OnChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceRowViewModel.Cells) or nameof(ResourceRowViewModel.Severity)) Update();
    }

    private void Update()
    {
        Text = _row is not null && _index < _row.Cells.Length ? _row.Cells[_index] : "";
        var tip = _row is not null && _index < _row.Tips.Length ? _row.Tips[_index] : null;
        ToolTip.SetTip(this, string.IsNullOrEmpty(Text) ? null : tip ?? Text);
        if (_colored && _row is not null)
            Foreground = _row.Severity is Severity.Neutral ? null : Converters.Brush(_row.Severity switch { Severity.Ok => "KubismOkBrush", Severity.Warning => "KubismWarnBrush", _ => "KubismErrorBrush" });
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_row is not null) _row.PropertyChanged -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_row is not null) { _row.PropertyChanged -= OnChanged; _row.PropertyChanged += OnChanged; Update(); }
    }
}

public partial class ResourceListView : UserControl
{
    private ResourceListViewModel? _vm;
    private readonly Dictionary<DataGridRow, ResourceRowViewModel> _tracked = [];

    public ResourceListView()
    {
        InitializeComponent();
        Grid.Sorting += OnSorting;
        Grid.SelectionChanged += OnSelectionChanged;
        Grid.LoadingRow += OnLoadingRow;
        Grid.UnloadingRow += OnUnloadingRow;
        Grid.KeyDown += OnKeyDown;
        Grid.ContextRequested += OnContextRequested;
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm is not null) { _vm.ColumnsChanged -= BuildColumns; _vm.PropertyChanged -= OnVmChanged; _vm.RevealRequested -= Reveal; }
        _vm = DataContext as ResourceListViewModel;
        if (_vm is null) return;
        _vm.ColumnsChanged += BuildColumns;
        _vm.PropertyChanged += OnVmChanged;
        _vm.RevealRequested += Reveal;
        BuildColumns();
    }

    /// <summary>A row selected by code (Back, a link from a graph) is selected in the grid too, so it is highlighted.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResourceListViewModel.SelectedRow) && _vm?.SelectedRow is { } row && !ReferenceEquals(Grid.SelectedItem, row) && _vm.Rows.Contains(row))
            Grid.SelectedItem = row;
    }

    private void Reveal(ResourceRowViewModel row) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Grid.SelectedItem == row) Grid.ScrollIntoView(row, Grid.Columns.FirstOrDefault()); }, Avalonia.Threading.DispatcherPriority.Loaded);

    private void BuildColumns()
    {
        if (_vm is null) return;
        var cols = _vm.Columns;
        Grid.Columns.Clear();
        for (var i = 0; i < cols.Count; i++)
        {
            var idx = i; var def = cols[i];
            var colored = def.Name is "Status" or "Phase" || (_vm.Kind.Kind == "Event" && def.Name == "Type");
            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = HeaderFor(def), Tag = idx, CanUserSort = true,
                Width = WidthFor(def),
                MinWidth = def.Name is "Name" ? 170 : def.Name == "Message" ? 200 : 50,
                CellTemplate = new FuncDataTemplate<ResourceRowViewModel>((_, _) => new CellTextBlock(idx, colored)),
            });
        }
    }

    private object HeaderFor(ColumnDef def)
    {
        var tip = _vm?.HeaderTip(def);
        if (tip is null) return def.Name;
        var tb = new TextBlock { Text = def.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(tb, tip);
        return tb;
    }

    private static DataGridLength WidthFor(ColumnDef def) => def.Name switch
    {
        "Name" => new DataGridLength(2, DataGridLengthUnitType.Star),
        "Namespace" => new DataGridLength(130),
        "Status" or "Phase" or "Type" => new DataGridLength(130),
        "Age" or "Ready" or "Restarts" or "Desired" or "Current" or "Available" or "Up-to-date" => new DataGridLength(85),
        "CPU" or "Memory" or "CPU usage" or "Memory usage" => new DataGridLength(170),
        "Message" => new DataGridLength(3, DataGridLengthUnitType.Star),
        _ => new DataGridLength(120),
    };

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_vm is not null && e.Column.Tag is int i) _vm.ToggleSort(i);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedRows.Clear();
        foreach (var r in Grid.SelectedItems.OfType<ResourceRowViewModel>()) _vm.SelectedRows.Add(r);
        _vm.SelectedRow = Grid.SelectedItems.Count == 1 ? Grid.SelectedItem as ResourceRowViewModel : Grid.SelectedItems.OfType<ResourceRowViewModel>().FirstOrDefault();
        _vm.NotifySelectionChanged();
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not ResourceRowViewModel row) return;
        _tracked[e.Row] = row;
        row.PropertyChanged += OnRowChanged;
        ApplyRowClasses(e.Row, row);
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (_tracked.Remove(e.Row, out var row)) row.PropertyChanged -= OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ResourceRowViewModel.IsNew) or nameof(ResourceRowViewModel.IsDeleting))) return;
        foreach (var (gridRow, row) in _tracked) if (ReferenceEquals(row, sender)) ApplyRowClasses(gridRow, row);
    }

    private static void ApplyRowClasses(DataGridRow gr, ResourceRowViewModel row)
    {
        gr.Classes.Set("newrow", row.IsNew);
        gr.Classes.Set("deleting", row.IsDeleting);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var obj = _vm.SelectedRow?.Item.Object;
        if (e.Key == Key.Delete) { _vm.DeleteSelectedCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.E && obj is not null) { _vm.Workspace.Details.BeginEditCommand.Execute(null); e.Handled = true; }
        else if (!ctrl && e.Key == Key.L && obj is not null)
        {
            _ = _vm.Kind.Kind == "Pod" ? _vm.Workspace.OpenPodLogsAsync(obj) : _vm.Workspace.OpenWorkloadLogsAsync(obj);
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.S && obj is not null && _vm.Kind.Kind == "Pod") { _ = _vm.Workspace.OpenShellAsync(obj); e.Handled = true; }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm?.SelectedRow is not { } row) { e.Handled = true; return; }
        var obj = row.Item.Object;
        var actions = _vm.Workspace.ActionsFor(_vm.Kind, obj);
        var menu = new ContextMenu();
        foreach (var a in actions)
        {
            if (a.IsSeparator) { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = a.Header, InputGesture = a.Gesture is null ? null : SafeGesture(a.Gesture), Command = a.Command, IsEnabled = a.IsEnabled };
            ToolTip.SetTip(item, a.Tooltip);
            a.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ActionItem.IsEnabled)) item.IsEnabled = a.IsEnabled;
                if (args.PropertyName == nameof(ActionItem.Tooltip)) ToolTip.SetTip(item, a.Tooltip);
            };
            menu.Items.Add(item);
        }
        _ = _vm.Workspace.ApplyRbacAsync(_vm.Kind, obj, actions);
        menu.Open(Grid);
        e.Handled = true;
    }

    private static KeyGesture? SafeGesture(string g) { try { return KeyGesture.Parse(g); } catch { return null; } }
}
