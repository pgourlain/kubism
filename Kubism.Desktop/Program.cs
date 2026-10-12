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
        // Anything that escapes every catch (an async void handler, a background thread, a forgotten task) ends up in log.txt instead of vanishing.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Kubism.Core.AppLog.Unhandled(e.IsTerminating ? "AppDomain, terminating" : "AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Kubism.Core.AppLog.Unhandled("unobserved task", e.Exception); e.SetObserved(); };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
