using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Views;

public partial class ImagesView : UserControl
{
    private ImagesViewModel? _vm;

    public ImagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.RevealRequested -= Reveal;
            _vm = DataContext as ImagesViewModel;
            if (_vm is not null) _vm.RevealRequested += Reveal;
        };
    }

    /// <summary>After a restore the selected row may be far down a long list: scroll it into view once the grid has laid out.</summary>
    private void Reveal(object item) => Dispatcher.UIThread.Post(() =>
    {
        if (RowsGrid.IsVisible && RowsGrid.SelectedItem == item) RowsGrid.ScrollIntoView(item, RowsGrid.Columns.FirstOrDefault());
    }, DispatcherPriority.Loaded);

    private void OnUserDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && (sender as ListBox)?.SelectedItem is ImageUserRow row) _vm.OpenUserCommand.Execute(row);
    }
}
