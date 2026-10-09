namespace Kubism.Desktop.Shell;

public static class ShellEnvironmentSetup
{
    /// <summary>GUI apps launched from Finder/Dock don't inherit the login shell PATH: resolve it once, in the background, so exec plugins are found.</summary>
    public static void Apply() => Task.Run(Kubism.Kube.ShellEnvironment.ApplyLoginPath);
}
