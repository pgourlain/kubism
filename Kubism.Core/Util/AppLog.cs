namespace Kubism.Core;

/// <summary>Minimal file log in the app-data folder. Never log bodies, tokens or secret values.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static string? Path { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var path = Path;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTime.Now:O} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
