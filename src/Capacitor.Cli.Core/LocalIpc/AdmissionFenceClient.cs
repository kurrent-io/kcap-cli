using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>Acquires a daemon's rename fence over its local control socket.</summary>
public static class AdmissionFenceClient {
    public static async Task<AdmissionFenceAcquireResult> AcquireAsync(
            DaemonStore store, string daemonName, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
        var path = store.SocketPath(daemonName);

        // Hello and acquire are separate connections: the control server routes one opening frame each.
        HelloReplyDto? hello;
        try {
            await using var helloConn = await Connection.OpenAsync(path, timeout, time, ct);
            var reply = await helloConn.ExchangeAsync(new LocalFrame(FrameType.Hello), timeout, ct);
            hello = reply?.Type == FrameType.HelloReply
                ? JsonSerializer.Deserialize(reply.Text, HelloIpcJsonContext.Default.HelloReplyDto)
                : null;
        } catch (Exception ex) when (IsTransport(ex, ct)) {
            return new(AdmissionFenceOutcome.Unavailable, null, ex.Message);
        }

        if (hello is null) return new(AdmissionFenceOutcome.Unavailable, null, "no hello reply");
        if (!(hello.Capabilities?.Contains(AdmissionFenceWire.Capability) ?? false))
            return new(AdmissionFenceOutcome.Unsupported, null, hello.DaemonVersion);

        Connection? conn = null;
        try {
            conn = await Connection.OpenAsync(path, timeout, time, ct);
            var request = JsonSerializer.Serialize(new AdmissionFenceAcquireDto(daemonName), AdmissionFenceIpcJsonContext.Default.AdmissionFenceAcquireDto);
            var ack = Parse(await conn.ExchangeAsync(LocalFrame.FenceJson(FrameType.AdmissionFenceAcquire, request), timeout, ct));

            if (ack is { Ok: true, State: AdmissionFenceWire.Held }) {
                var session = new Session(conn, ack.Pid, time);
                conn = null;
                return new(AdmissionFenceOutcome.Acquired, session);
            }
            return ack?.Reason == AdmissionFenceWire.Busy
                ? new(AdmissionFenceOutcome.Busy, null)
                : new(AdmissionFenceOutcome.Unavailable, null, ack?.Reason ?? "unexpected reply");
        } catch (Exception ex) when (IsTransport(ex, ct)) {
            return new(AdmissionFenceOutcome.Unavailable, null, ex.Message);
        } finally {
            if (conn is not null) await conn.DisposeAsync();
        }
    }

    static AdmissionFenceAckDto? Parse(LocalFrame? frame) {
        if (frame?.Type != FrameType.AdmissionFenceAck) return null;
        try { return JsonSerializer.Deserialize(frame.Text, AdmissionFenceIpcJsonContext.Default.AdmissionFenceAckDto); }
        catch (JsonException) { return null; }
    }

    static bool IsTransport(Exception ex, CancellationToken ct) =>
        ex is IOException or SocketException or InvalidDataException or JsonException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    sealed class Session(Connection conn, int? pid, TimeProvider time) : IAdmissionFenceSession {
        public int? Pid { get; } = pid;

        public async Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct) {
            try {
                var ack = Parse(await conn.ExchangeAsync(new LocalFrame(FrameType.AdmissionFenceCommit), timeout, ct));
                return ack is { Ok: true, State: AdmissionFenceWire.Committed };
            } catch (Exception ex) when (IsTransport(ex, ct)) {
                return false;
            }
        }

        public async Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct) {
            using var deadline = new CancellationTokenSource(timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try {
                await conn.WriteAsync(new LocalFrame(FrameType.AdmissionFenceAbort), linked.Token);
                // A commit answer that arrived late may precede the abort's own.
                while (Parse(await conn.ReadAsync(linked.Token)) is { } ack) {
                    if (ack.State == AdmissionFenceWire.Aborted) return true;
                }
                return false;
            } catch (Exception ex) when (IsTransport(ex, ct)) {
                return false;
            }
        }

        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }

    sealed class Connection : IAsyncDisposable {
        readonly Socket _socket;
        readonly NetworkStream _stream;
        readonly TimeProvider _time;

        Connection(Socket socket, TimeProvider time) {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: true);
            _time = time;
        }

        public static async Task<Connection> OpenAsync(string path, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                using var deadline = new CancellationTokenSource(timeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), linked.Token);
                return new Connection(socket, time);
            } catch {
                socket.Dispose();
                throw;
            }
        }

        public async Task<LocalFrame?> ExchangeAsync(LocalFrame request, TimeSpan timeout, CancellationToken ct) {
            using var deadline = new CancellationTokenSource(timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            await FrameCodec.WriteAsync(_stream, request, linked.Token);
            return await FrameCodec.ReadAsync(_stream, linked.Token);
        }

        public Task WriteAsync(LocalFrame frame, CancellationToken ct) => FrameCodec.WriteAsync(_stream, frame, ct);

        public Task<LocalFrame?> ReadAsync(CancellationToken ct) => FrameCodec.ReadAsync(_stream, ct);

        public async ValueTask DisposeAsync() {
            await _stream.DisposeAsync();
            _socket.Dispose();
        }
    }
}
