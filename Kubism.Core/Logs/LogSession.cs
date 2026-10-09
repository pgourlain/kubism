using System.Globalization;
using Kubism.Core.Api;

namespace Kubism.Core.Logs;

public sealed record LogOptions(string Namespace, string Pod, string? Container = null, bool Follow = true, bool Previous = false,
    int? TailLines = 1000, int? SinceSeconds = null);

public enum LogState { Idle, Streaming, Ended, Error }

/// <summary>Streams one container's log into a bounded buffer; reconnects while the pod is still running.</summary>
public sealed class LogSession : IAsyncDisposable
{
    private readonly IKubeTransport _transport;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public LogSession(IKubeTransport transport, LogOptions options, LogBuffer buffer, string? source = null)
    {
        _transport = transport; Options = options; Buffer = buffer;
        Source = source ?? (options.Container is null ? options.Pod : $"{options.Pod}/{options.Container}");
    }

    public LogOptions Options { get; }
    public LogBuffer Buffer { get; }
    public string Source { get; }
    public LogState State { get; private set; } = LogState.Idle;
    public string? Error { get; private set; }
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public event Action<LogState>? StateChanged;

    public void Start(CancellationToken parent = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct));
    }

    public static string BuildPath(LogOptions o, DateTimeOffset? sinceTime = null)
    {
        var q = new List<string> { "timestamps=true" };
        if (o.Follow) q.Add("follow=true");
        if (o.Container is not null) q.Add($"container={Uri.EscapeDataString(o.Container)}");
        if (o.Previous) q.Add("previous=true");
        if (sinceTime is not null) q.Add($"sinceTime={Uri.EscapeDataString(sinceTime.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}");
        else
        {
            if (o.TailLines is { } t) q.Add($"tailLines={t}");
            if (o.SinceSeconds is { } s) q.Add($"sinceSeconds={s}");
        }
        return $"{Gvr.Pods.ObjectPath(o.Namespace, o.Pod, "log")}?{string.Join("&", q)}";
    }

    private void SetState(LogState s, string? error = null)
    {
        State = s; Error = error;
        StateChanged?.Invoke(s);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset? last = null;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(LogState.Streaming);
                await using var stream = await _transport.StreamAsync(BuildPath(Options, first ? null : last), ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    var (time, text) = Parse(line);
                    if (!first && time is not null && last is not null && time <= last) continue; // duplicate after reconnect
                    if (time is not null) last = time;
                    Buffer.Add(time, text, Source);
                }
                first = false;
                if (!Options.Follow || Options.Previous) { SetState(LogState.Ended); return; }
                // Stream dropped: keep going only while the pod still exists and isn't finished.
                if (!await PodRunningAsync(ct).ConfigureAwait(false)) { SetState(LogState.Ended); return; }
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (KubeApiException ex) when (ex.StatusCode is 400 or 404)
            {
                SetState(LogState.Error, ex.Message); return;
            }
            catch (Exception ex)
            {
                SetState(LogState.Error, ex.Message);
                if (!Options.Follow) return;
                try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                first = false;
            }
        }
    }

    private async Task<bool> PodRunningAsync(CancellationToken ct)
    {
        try
        {
            var pod = await _transport.GetAsync(Gvr.Pods.ObjectPath(Options.Namespace, Options.Pod), ct).ConfigureAwait(false);
            var phase = pod?["status"]?["phase"]?.ToString();
            return phase is "Running" or "Pending" or null;
        }
        catch (KubeApiException ex) when (ex.StatusCode == 404) { return false; }
    }

    /// <summary>Splits "2026-01-01T00:00:00.123456789Z message" into time and message.</summary>
    public static (DateTimeOffset? time, string text) Parse(string line)
    {
        var sp = line.IndexOf(' ');
        if (sp > 19 && sp < 40 && line[4] == '-' && line[10] == 'T' &&
            DateTimeOffset.TryParse(line.AsSpan(0, sp), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return (t, line[(sp + 1)..]);
        return (null, line);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_task is not null) { try { await _task.ConfigureAwait(false); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
