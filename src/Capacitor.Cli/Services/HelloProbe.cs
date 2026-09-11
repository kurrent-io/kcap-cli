using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

/// <param name="WellFormed">The daemon answered a parseable HelloReply — the strong signal
/// install-verify and start-verify gate on.</param>
public sealed record HelloProbeResult(bool WellFormed, int? ProtocolVersion, string? DaemonVersion, string? DaemonName) {
    /// <summary>The control socket accepted the connection, whether or not a HelloReply followed. This
    /// is the SERVING signal: a daemon built before the Hello frame accepts the connection and drops
    /// the unknown frame without replying, so it is reachable and serving yet not well-formed. Only a
    /// connection that never opened — refused, timed out, no socket — is unreachable, and only that is
    /// still starting. Defaults false so a well-formed test fake that never dialed a socket does not
    /// falsely read as reachable; <see cref="HelloProbe.RunAsync"/> is the only place it is set true.</summary>
    public bool Reachable { get; init; }
}

/// <summary>
/// One-shot dial + Hello + HelloReply against a daemon's local control socket, bounded by
/// <c>timeout</c>. Mirrors the hello leg of <c>LocalControlClient.RunCycleAsync</c>
/// but deliberately WITHOUT its <c>status/1</c> capability gate — that gate is exactly what
/// install-verify (which validates version itself) and start-verify (which must accept a
/// capability-incompatible hello) must not apply.
/// </summary>
static class HelloProbe {
    static readonly HelloProbeResult Unreachable            = new(false, null, null, null);
    static readonly HelloProbeResult ReachableNotWellFormed = new(false, null, null, null) { Reachable = true };

    public static async Task<HelloProbeResult> RunAsync(DaemonStore store, string daemonName, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);

        NetworkStream stream;
        try {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            stream = await ConnectAsync(sock, store, daemonName, cts.Token);
        } catch {
            // Never opened: refused, timed out, or no socket at all — the daemon has not bound its
            // listener, so it is starting, not serving.
            return Unreachable;
        }

        // The connection opened, so the daemon is reachable — serving — from here on, even if the
        // exchange below fails. An older daemon drops the unknown Hello frame without replying.
        await using (stream) {
            try {
                await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.Hello), cts.Token);
                var reply = await FrameCodec.ReadAsync(stream, cts.Token);
                if (reply is null || reply.Type != FrameType.HelloReply) return ReachableNotWellFormed;

                var dto = JsonSerializer.Deserialize(reply.Text, HelloIpcJsonContext.Default.HelloReplyDto);
                if (dto is null) return ReachableNotWellFormed;
                return new HelloProbeResult(true, dto.ProtocolVersion, dto.DaemonVersion, dto.DaemonName) { Reachable = true };
            } catch {
                return ReachableNotWellFormed;
            }
        }
    }

    static async Task<NetworkStream> ConnectAsync(Socket sock, DaemonStore store, string daemonName, CancellationToken ct) {
        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
            return new NetworkStream(sock, ownsSocket: true);
        } catch { sock.Dispose(); throw; }
    }
}
