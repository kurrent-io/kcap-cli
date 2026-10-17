using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A daemon control socket that accepts connections one at a time, reads one frame on each and
/// answers it.</summary>
static class FakeControlSocket {
    /// <param name="reply">Null closes the connection without answering, as a daemon that cannot decode the frame does.</param>
    /// <param name="afterReply">Runs once the reply is written, before the connection closes.</param>
    /// <returns>The frame the client sent.</returns>
    public static async Task<LocalFrame?> AnswerOnce(DaemonStore store, string name, LocalFrame? reply, Action? afterReply = null) =>
        (await AnswerInOrder(store, name, [reply], afterReply))[0];

    /// <summary>Answers one connection per reply, in order; a null reply closes that connection unanswered.</summary>
    /// <param name="afterReply">Runs once the last reply is written.</param>
    /// <returns>The frames the client sent, one per connection.</returns>
    public static Task<IReadOnlyList<LocalFrame?>> AnswerInOrder(
            DaemonStore store, string name, IReadOnlyList<LocalFrame?> replies, Action? afterReply = null) {
        store.EnsureDirectory();
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(store.SocketPath(name)));
        listener.Listen(replies.Count);

        return Task.Run<IReadOnlyList<LocalFrame?>>(async () => {
            var received = new List<LocalFrame?>();

            using (listener) {
                foreach (var reply in replies) {
                    using var connection = await listener.AcceptAsync();
                    await using var stream = new NetworkStream(connection, ownsSocket: false);
                    received.Add(await FrameCodec.ReadAsync(stream, CancellationToken.None));
                    if (reply is not null) await FrameCodec.WriteAsync(stream, reply, CancellationToken.None);
                }

                afterReply?.Invoke();
            }

            return received;
        });
    }
}
