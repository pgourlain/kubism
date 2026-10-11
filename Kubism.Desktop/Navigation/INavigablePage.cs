namespace Kubism.Desktop.Navigation;

/// <summary>A page that can report what it shows (filter, tab, selection…) when the user leaves it, so Back can put it back.</summary>
public interface INavigablePage
{
    object? CaptureState();
}
