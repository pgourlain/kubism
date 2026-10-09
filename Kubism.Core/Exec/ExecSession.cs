using System.Text;
using System.Text.Json.Nodes;
using Kubism.Core.Api;

namespace Kubism.Core.Exec;

public sealed record ExecResult(bool Success, int? ExitCode, string? Message);

/// <summary>WebSocket exec/attach over the channel protocol (v5, falling back to v4): TTY, stdin, resize channel (4), status channel (3).</summary>
public sealed class ExecSession : IAsyncDisposable
{
    public static readonly string[] SubProtocols = ["v5.channel.k8s.io", "v4.channel.k8s.io", "v3.channel.k8s.io", "channel.k8s.io"];
    /// <summary>Starts bash when present, otherwise sh (avoids a failed first attempt).</summary>
    public static readonly string[] DefaultShell = ["/bin/sh", "-c", "command -v bash >/dev/null 2>&1 && exec bash || exec sh"];

    private readonly IKubeTransport _transport;
    private IKubeSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _reader;

    public ExecSession(IKubeTransport transport, string ns, string pod, string? container, IReadOnlyList<string>? command = null, bool tty = true)
    {
        _transport = transport; Namespace = ns; Pod = pod; Container = container; Command = command ?? DefaultShell; Tty = tty;
    }

    public string Namespace { get; }
    public string Pod { get; }
    public string? Container { get; }
    public IReadOnlyList<string> Command { get; }
    public bool Tty { get; }
    public string? Protocol => _socket?.SubProtocol;
    public ExecResult? Result { get; private set; }
    public bool IsRunning => _socket is not null && Result is null;
    /// <summary>Raised for stdout/stderr (channel 1/2) data.</summary>
    public event Action<byte[], int>? Output;
    public event Action<ExecResult>? Exited;

    public static string BuildPath(string ns, string pod, string? container, IReadOnlyList<string> command, bool tty, bool stdin = true)
    {
        var q = new List<string>();
        if (container is not null) q.Add($"container={Uri.EscapeDataString(container)}");
        foreach (var c in command) q.Add($"command={Uri.EscapeDataString(c)}");
        q.Add($"stdin={(stdin ? "true" : "false")}");
        q.Add("stdout=true");
        q.Add($"stderr={(tty ? "false" : "true")}");
        q.Add($"tty={(tty ? "true" : "false")}");
        return $"{Gvr.Pods.ObjectPath(ns, pod, "exec")}?{string.Join("&", q)}";
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socket = await _transport.OpenSocketAsync(BuildPath(Namespace, Pod, Container, Command, Tty), SubProtocols, _cts.Token).ConfigureAwait(false);
        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Length == 0) continue;
                var channel = msg[0];
                var payload = msg.AsSpan(1).ToArray();
                if (channel is 1 or 2) Output?.Invoke(payload, channel);
                else if (channel == 3) { Finish(ParseStatus(payload)); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Finish(new ExecResult(false, null, ex.Message)); return; }
        Finish(new ExecResult(true, null, "Connection closed"));
    }

    private void Finish(ExecResult r)
    {
        if (Result is not null) return;
        Result = r;
        Exited?.Invoke(r);
    }

    public static ExecResult ParseStatus(byte[] payload)
    {
        try
        {
            var j = JsonNode.Parse(payload) as JsonObject;
            var status = j?["status"]?.ToString();
            if (status == "Success") return new ExecResult(true, 0, null);
            int? code = null;
            foreach (var c in (j?["details"]?["causes"] as JsonArray ?? []).OfType<JsonObject>())
                if (c["reason"]?.ToString() == "ExitCode" && int.TryParse(c["message"]?.ToString(), out var n)) code = n;
            var msg = j?["message"]?.ToString();
            if (msg is not null && msg.Contains("executable file not found"))
                msg += " (the container has no shell; distroless images can't be exec'd, try an ephemeral debug container)";
            return new ExecResult(false, code, msg);
        }
        catch { return new ExecResult(false, null, Encoding.UTF8.GetString(payload)); }
    }

    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => SendAsync(0, data, ct);
    public Task SendInputAsync(string text, CancellationToken ct = default) => SendAsync(0, Encoding.UTF8.GetBytes(text), ct);

    public Task ResizeAsync(int width, int height, CancellationToken ct = default) =>
        SendAsync(4, Encoding.UTF8.GetBytes(new JsonObject { ["Width"] = width, ["Height"] = height }.ToJsonString()), ct);

    private Task SendAsync(byte channel, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Exec session not started");
        var msg = new byte[data.Length + 1];
        msg[0] = channel;
        data.CopyTo(msg.AsMemory(1));
        return _socket.SendAsync(msg, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_socket is not null) await _socket.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null) { try { await _reader.ConfigureAwait(false); } catch { } }
        _cts?.Dispose();
    }
}
