using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary>Sends one restart request over a daemon's control socket.</summary>
static class DaemonRestartClient {
    /// <summary>The daemon's reply, or null when it closed the socket without one. An unreachable socket
    /// throws <see cref="SocketException"/> or <see cref="IOException"/>.</summary>
    public static async Task<LocalFrame?> RequestAsync(
            DaemonStore store, string name, string mode, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(name)), ct);

        await using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, LocalFrame.Restart(mode), ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }
}
