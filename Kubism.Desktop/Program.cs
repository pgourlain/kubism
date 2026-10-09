using Avalonia;

namespace Kubism.Desktop;

public static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // `Kubism --mcp` runs headless over stdio: nothing may be written to stdout outside the protocol.
        if (args.Contains("--mcp"))
        {
            Kube.ShellEnvironment.ApplyLoginPath();
            return await Mcp.McpHost.RunAsync(args, new Kube.KubeTransportFactory(), new Kube.HelmCliRunnerFactory(), new Kube.AzureCliTokenProvider());
        }

        AppOptions.Current = AppOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
