using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kubism.Desktop.ViewModels;

/// <summary>A menu/button action on a resource. Disabled actions carry the reason as tooltip (RBAC, read-only).</summary>
public sealed partial class ActionItem : ViewModelBase
{
    public ActionItem(string header, Func<Task> execute, string? gesture = null, bool destructive = false)
    {
        Header = header; Command = new AsyncRelayCommand(execute, () => IsEnabled); Gesture = gesture; IsDestructive = destructive;
    }

    private ActionItem() { Header = ""; Command = new RelayCommand(() => { }); IsSeparator = true; }
    public static ActionItem Separator() => new();

    public string Header { get; }
    public ICommand Command { get; }
    public string? Gesture { get; }
    public bool IsDestructive { get; }
    public bool IsSeparator { get; }
    /// <summary>RBAC check performed before the action is offered (verb, subresource).</summary>
    public string? Verb { get; init; }
    public string? Subresource { get; init; }
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string? _tooltip;

    partial void OnIsEnabledChanged(bool value) => (Command as AsyncRelayCommand)?.NotifyCanExecuteChanged();

    public ActionItem Disable(string reason) { IsEnabled = false; Tooltip = reason; return this; }
}
