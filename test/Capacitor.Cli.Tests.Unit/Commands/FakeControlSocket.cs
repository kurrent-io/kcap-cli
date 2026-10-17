using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A daemon control socket that accepts one connection, reads one frame and answers it.</summary>
static class FakeControlSocket {
    /// <param name="afterReply">Runs once the reply is written, before the connection closes.</param>
    /// <returns>The frame the client sent.</returns>
    public static Task<LocalFrame?> AnswerOnce(DaemonStore store, string name, LocalFrame reply, Action? afterReply = null) {
        store.EnsureDirectory();
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(store.SocketPath(name)));
        listener.Listen(1);

        return Task.Run(async () => {
            using (listener) {
                using var connection = await listener.AcceptAsync();
                await using var stream = new NetworkStream(connection, ownsSocket: false);
                var received = await FrameCodec.ReadAsync(stream, CancellationToken.None);
                await FrameCodec.WriteAsync(stream, reply, CancellationToken.None);
                afterReply?.Invoke();

                return received;
            }
        });
    }
}
