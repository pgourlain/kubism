namespace Kubism.Core;

/// <summary>Minimal file log in the app-data folder. Never log bodies, tokens or secret values.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static string? Path { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", Format(message, ex));
    public static void Error(string message, Exception? ex = null) => Write("ERROR", Format(message, ex));

    /// <summary>Full <see cref="Exception.ToString"/>: type, message, stack trace and inner exceptions.</summary>
    private static string Format(string message, Exception? ex) => ex is null ? message : $"{message}: {ex}";

    /// <summary>An exception nobody caught: logged with its full stack trace (and inner exceptions), so a crash report is complete. Messages only, never request data.</summary>
    public static void Unhandled(string source, Exception? ex) => Write("FATAL", $"Unhandled exception ({source}): {ex?.ToString() ?? "unknown"}");

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
