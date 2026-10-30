using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Tests.Helpers;

/// <summary>A daemon control socket that answers hello with the capabilities it is given and every
/// AgentSession frame with one fixed session, one connection at a time — or, with no session, holds an
/// AgentSession frame unanswered until disposed, as a daemon still waiting for the agent to be bound
/// does. Records each frame type it reads.</summary>
public sealed class FakeAgentSessionDaemon : IAsyncDisposable {
    readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    readonly CancellationTokenSource _cts = new();
    readonly List<FrameType> _received = [];
    readonly Task _accept;

    public FakeAgentSessionDaemon(string socketPath, IReadOnlyList<string> capabilities, string? session) {
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);
        _accept = Task.Run(() => ServeAsync(capabilities, session));
    }

    public IReadOnlyList<FrameType> Received {
        get { lock (_received) return [.. _received]; }
    }

    async Task ServeAsync(IReadOnlyList<string> capabilities, string? session) {
        var hello = JsonSerializer.Serialize(
            new HelloReplyDto(HelloProtocol.CurrentVersion, "test", "fake", [.. capabilities]), HelloIpcJsonContext.Default.HelloReplyDto);

        try {
            while (true) {
                using var conn = await _listener.AcceptAsync(_cts.Token);
                await using var stream = new NetworkStream(conn, ownsSocket: false);
                if (await FrameCodec.ReadAsync(stream, _cts.Token) is not { } frame) continue;

                lock (_received) _received.Add(frame.Type);
                if (frame.Type != FrameType.Hello && session is null) await Task.Delay(Timeout.Infinite, _cts.Token);

                var reply = frame.Type == FrameType.Hello
                    ? LocalFrame.HelloJson(FrameType.HelloReply, hello)
                    : LocalFrame.AgentSessionReply(session);
                await FrameCodec.WriteAsync(stream, reply, _cts.Token);
            }
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) {
            // Disposed: the listener is gone.
        }
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync();
        _listener.Dispose();
        await _accept;
        _cts.Dispose();
    }
}
