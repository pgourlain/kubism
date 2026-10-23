using System.Diagnostics;

namespace Kubism.Kube;

/// <summary>GUI apps launched from Finder/Dock don't inherit the login shell PATH, so exec plugins (kubelogin, aws…) aren't found.</summary>
public static class ShellEnvironment
{
    private static string? _loginPath;
    private static bool _resolved;
    private static readonly object Gate = new();

    public static string? ResolveLoginPath(TimeSpan? timeout = null)
    {
        lock (Gate)
        {
            if (_resolved) return _loginPath;
            _resolved = true;
            if (OperatingSystem.IsWindows()) return _loginPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/sh";
                var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("-l"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("echo \"__KUBISM_PATH__$PATH\"");
                using var p = Process.Start(psi)!;
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeout ?? TimeSpan.FromSeconds(3))) { try { p.Kill(true); } catch { } return _loginPath = null; }
                var text = outTask.GetAwaiter().GetResult();
                var line = text.Split('\n').LastOrDefault(l => l.Contains("__KUBISM_PATH__"));
                return _loginPath = line?[(line.IndexOf("__KUBISM_PATH__", StringComparison.Ordinal) + "__KUBISM_PATH__".Length)..].Trim();
            }
            catch { return _loginPath = null; }
        }
    }

    /// <summary>Merges the login-shell PATH into this process's PATH so spawned plugins find their binaries.</summary>
    public static void ApplyLoginPath()
    {
        var login = ResolveLoginPath();
        if (string.IsNullOrEmpty(login)) return;
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = login.Split(Path.PathSeparator).Concat(current.Split(Path.PathSeparator)).Where(p => p.Length > 0).Distinct();
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, parts));
    }

    public static string? Which(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        if (command.Contains(Path.DirectorySeparatorChar) && File.Exists(command)) return command;
        foreach (var dir in path.Split(Path.PathSeparator))
            foreach (var e in exts)
            {
                var f = Path.Combine(dir, command + e);
                if (File.Exists(f)) return f;
            }
        return null;
    }
}
