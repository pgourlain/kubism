using Kubism.Desktop.Services;
using Kubism.Desktop.ViewModels;

namespace Kubism.Desktop.Tests;

/// <summary>Scripted dialogs for tests; every shown dialog is recorded.</summary>
public sealed class FakeDialogService : IDialogService
{
    public List<string> Shown { get; } = [];
    public bool ConfirmResult { get; set; } = true;
    public Func<string, string?> TypedResponse { get; set; } = expected => expected;
    public Func<string, string?>? PromptResponse { get; set; }
    public Func<IReadOnlyList<string>, string?> ChooseResponse { get; set; } = o => o.FirstOrDefault();
    public string? SavePath { get; set; }
    public string? FilePath { get; set; }
    public string? FolderPath { get; set; }
    public List<string> SignInLines { get; } = [];
    public int SignInShown { get; private set; }

    public Task<bool> ConfirmAsync(ConfirmRequest r) { Shown.Add($"confirm:{r.Title}:{r.Message}"); return Task.FromResult(ConfirmResult); }
    public Task<string?> ConfirmTypedAsync(string title, string message, string expected) { Shown.Add($"typed:{title}:{expected}"); return Task.FromResult(TypedResponse(expected)); }
    public Task ShowErrorAsync(string title, string message) { Shown.Add($"error:{title}:{message}"); return Task.CompletedTask; }
    public AboutInfo? About { get; private set; }
    public Task ShowAboutAsync(AboutInfo info) { About = info; Shown.Add("about"); return Task.CompletedTask; }
    public Task ShowTextAsync(string title, string text) { Shown.Add($"text:{title}:{text}"); return Task.CompletedTask; }
    public Task<string?> PromptAsync(string title, string message, string? initial = null) { Shown.Add($"prompt:{title}"); return Task.FromResult(PromptResponse?.Invoke(initial ?? "") ?? initial); }
    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> options) { Shown.Add($"choose:{title}"); return Task.FromResult(ChooseResponse(options)); }
    public Task<ClusterEditorViewModel?> EditClusterAsync(ClusterEditorViewModel editor) { Shown.Add("editcluster"); return Task.FromResult<ClusterEditorViewModel?>(editor); }
    public SettingsEditorViewModel? SettingsEdit { get; set; }
    public Task<SettingsEditorViewModel?> EditSettingsAsync(SettingsEditorViewModel editor) { Shown.Add("settings"); SettingsEdit?.GetType(); return Task.FromResult<SettingsEditorViewModel?>(editor); }
    public Task<string?> PickFileAsync(string title) => Task.FromResult(FilePath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderPath);
    public Task<string?> SaveFileAsync(string title, string suggestedName) => Task.FromResult(SavePath);
    public SignInHandle ShowSignIn(string cluster) { SignInShown++; return new SignInHandle(l => SignInLines.Add(l), CancellationToken.None, () => { }); }
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }
    public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
}

public sealed class FakeBrowser : IBrowserLauncher
{
    public List<string> Opened { get; } = [];
    public void Open(string url) => Opened.Add(url);
}

/// <summary>Runs posted actions inline when already on the UI thread, otherwise posts to the Avalonia dispatcher.</summary>
public sealed class TestUiThread : IUiThread
{
    private readonly AvaloniaUiThread _inner = new();
    public void Post(Action action) => _inner.Post(action);
    public Task InvokeAsync(Action action) => _inner.InvokeAsync(action);
    public bool CheckAccess() => _inner.CheckAccess();
}

public sealed class FakeHelmRunner : Kubism.Core.Helm.IHelmRunner
{
    public List<string[]> Calls { get; } = [];
    public List<string> Values { get; } = [];
    public bool IsAvailable { get; set; } = true;
    /// <summary>What a call prints, by its first argument ("template" output is the rendered manifest).</summary>
    public Dictionary<string, string> Output { get; } = new() { ["search"] = "[]" };
    public string? FailWith { get; set; }

    public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add([.. args]);
        var f = args.ToList().IndexOf("-f");
        if (f >= 0 && File.Exists(args[f + 1])) Values.Add(File.ReadAllText(args[f + 1]));
        if (FailWith is not null && args[0] is "template" or "upgrade") throw new InvalidOperationException(FailWith);
        return Task.FromResult(Output.GetValueOrDefault(args[0], "ok"));
    }
}

public sealed class FakeHelmFactory(FakeHelmRunner runner) : Kubism.Core.Helm.IHelmRunnerFactory
{
    public Kubism.Core.Helm.IHelmRunner Create(Kubism.Core.Catalog.ClusterEntry entry) => runner;
}
