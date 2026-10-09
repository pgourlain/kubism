using Avalonia.Threading;

namespace Kubism.Desktop.Services;

/// <summary>Marshals work onto the UI thread. View models never touch Dispatcher directly.</summary>
public interface IUiThread
{
    void Post(Action action);
    Task InvokeAsync(Action action);
    bool CheckAccess();
}

public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public interface IBrowserLauncher
{
    void Open(string url);
}

public sealed record ConfirmRequest(string Title, string Message, string ConfirmText = "OK", bool Destructive = false);

public sealed record SignInHandle(Action<string> OnLine, CancellationToken Token, Action Close);

/// <summary>Dialogs. Tests use <c>FakeDialogService</c>.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(ConfirmRequest request);
    /// <summary>Production confirmation: the user must type <paramref name="expected"/>. Returns the typed text, or null if cancelled.</summary>
    Task<string?> ConfirmTypedAsync(string title, string message, string expected);
    Task ShowErrorAsync(string title, string message);
    Task ShowTextAsync(string title, string text);
    Task<string?> PromptAsync(string title, string message, string? initial = null);
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> options);
    Task<ViewModels.ClusterEditorViewModel?> EditClusterAsync(ViewModels.ClusterEditorViewModel editor);
    Task<string?> PickFileAsync(string title);
    Task<string?> PickFolderAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName);
    /// <summary>Shows the "Signing in to cluster" dialog for exec plugins with device-code prompts.</summary>
    SignInHandle ShowSignIn(string cluster);
}
