using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>Asks a daemon which session one of its hosted agents is bound to.</summary>
public static class AgentSessionIpc {
    /// <summary>Advertised by a daemon that answers <see cref="FrameType.AgentSession"/>.</summary>
    public const string Capability = "agent-session/1";

    /// <summary>The agent's session, or null when the daemon is unreachable, does not advertise
    /// <see cref="Capability"/>, hosts no such agent, or the agent has no session yet.</summary>
    /// <param name="timeout">One budget for the whole exchange, hello included.</param>
    public static async Task<string?> QueryAsync(
            DaemonStore store, string daemonName, string agentId, TimeProvider time, TimeSpan timeout,
            CancellationToken ct = default) {
        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linked = linkedCts.Token;

        try {
            var hello = await ExchangeAsync(store, daemonName, new LocalFrame(FrameType.Hello), linked);
            if (hello?.Type != FrameType.HelloReply) return null;
            var caps = JsonSerializer.Deserialize(hello.Text, HelloIpcJsonContext.Default.HelloReplyDto)?.Capabilities;
            if (caps is null || !caps.Contains(Capability)) return null;

            var reply = await ExchangeAsync(store, daemonName, LocalFrame.AgentSession(agentId), linked);

            return reply is { Type: FrameType.AgentSessionReply, Text.Length: > 0 } ? reply.Text : null;
        } catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or JsonException
                                     || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
            return null;
        }
    }

    static async Task<LocalFrame?> ExchangeAsync(DaemonStore store, string daemonName, LocalFrame request, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
        await using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, request, ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }
}
