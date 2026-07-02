// test/Capacitor.Cli.Tests.Unit/Acp/AcpConnectionTests.cs
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Exercises <see cref="AcpConnection"/> over an in-memory duplex "wire" so no real process is
/// spawned. Two independent <see cref="Pipe"/>s stand in for the agent's stdin/stdout:
/// <c>toAgent</c> is written by the connection (via its <c>writeStream</c>) and read by the test's
/// "agent side" harness; <c>toClient</c> is written by the test's "agent side" harness and read by
/// the connection (via its <c>readStream</c>). <see cref="Pipe"/> gives us a real blocking/async
/// reader — unlike <see cref="MemoryStream"/>, a read against an empty <see cref="Pipe"/> awaits
/// until a writer produces more bytes or completes the writer side, which is what a real pipe to a
/// child process does. Task 8 formalizes a richer fake agent on top of this same primitive; this
/// harness stays intentionally minimal and local to this test file.
/// </summary>
public class AcpConnectionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class Harness : IAsyncDisposable {
        readonly Pipe   _toAgent  = new();
        readonly Pipe   _toClient = new();
        readonly Stream _agentReadsFromClient;
        readonly Stream _agentWritesToClient;

        public AcpConnection Connection { get; }

        public Harness() {
            // Connection writes requests/notifications/responses into _toAgent; the "agent side"
            // (this harness) reads them from the same pipe.
            _agentReadsFromClient = _toAgent.Reader.AsStream();

            // The "agent side" writes frames into _toClient; the connection reads them as its
            // readStream.
            _agentWritesToClient = _toClient.Writer.AsStream();

            Connection = new AcpConnection(
                writeStream: _toAgent.Writer.AsStream(),
                readStream: _toClient.Reader.AsStream(),
                logger: NullLogger<AcpConnection>.Instance
            );
        }

        /// <summary>Reads one newline-delimited frame the connection wrote, as raw text.</summary>
        public async Task<string> ReadFrameFromConnectionAsync() {
            var line = await ReadLineAsync(_agentReadsFromClient).WaitAsync(HangGuard);
            return line ?? throw new InvalidOperationException("stream completed before a frame arrived");
        }

        /// <summary>Writes one newline-delimited frame as if the agent process emitted it.</summary>
        public async Task WriteFrameToConnectionAsync(string json) {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _agentWritesToClient.WriteAsync(bytes).AsTask().WaitAsync(HangGuard);
            await _agentWritesToClient.FlushAsync().WaitAsync(HangGuard);
        }

        static async Task<string?> ReadLineAsync(Stream stream) {
            var buffer = new List<byte>();
            var one    = new byte[1];

            while (true) {
                var n = await stream.ReadAsync(one);
                if (n == 0)
                    return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                if (one[0] == (byte) '\n')
                    return Encoding.UTF8.GetString(buffer.ToArray());

                buffer.Add(one[0]);
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    [Test]
    public async Task RequestAsync_resolves_with_result_on_matching_response() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("initialize", null, CancellationToken.None);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        var       id  = doc.RootElement.GetProperty("id").GetInt64();
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("initialize");

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"stopReason":"end_turn"}}"""
        );

        var result = await requestTask.WaitAsync(HangGuard);
        await Assert.That(result.GetProperty("stopReason").GetString()).IsEqualTo("end_turn");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Concurrent_requests_correlate_to_their_own_responses_when_interleaved() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestA = harness.Connection.RequestAsync("session/new", null, CancellationToken.None);
        var frameA   = await harness.ReadFrameFromConnectionAsync();
        var idA      = JsonDocument.Parse(frameA).RootElement.GetProperty("id").GetInt64();

        var requestB = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frameB   = await harness.ReadFrameFromConnectionAsync();
        var idB      = JsonDocument.Parse(frameB).RootElement.GetProperty("id").GetInt64();

        await Assert.That(idA).IsNotEqualTo(idB);

        // Respond out of order: B's response arrives before A's.
        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{idB}}},"result":{"marker":"B"}}"""
        );
        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{idA}}},"result":{"marker":"A"}}"""
        );

        var resultA = await requestA.WaitAsync(HangGuard);
        var resultB = await requestB.WaitAsync(HangGuard);

        await Assert.That(resultA.GetProperty("marker").GetString()).IsEqualTo("A");
        await Assert.That(resultB.GetProperty("marker").GetString()).IsEqualTo("B");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Error_response_throws_AcpRpcException_with_code_and_message() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("session/set_config_option", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32603,"message":"Internal error"}}"""
        );

        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex!.Code).IsEqualTo(-32603);
        await Assert.That(ex.Message).IsEqualTo("Internal error");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_notification_raises_OnNotification_with_method_and_params() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"abc","update":{"sessionUpdate":"agent_message_chunk"}}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Method).IsEqualTo("session/update");
        await Assert.That(notification.Params).IsNotNull();
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("abc");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_handler_set_invokes_handler_and_echoes_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (request, _) => {
            var result = JsonSerializer.SerializeToElement(new { content = "file contents" });
            return Task.FromResult<object?>(result);
        };

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"fs/read_text_file","params":{"path":"/tmp/x"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("content").GetString())
            .IsEqualTo("file contents");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_no_handler_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        // OnServerRequest intentionally left unset (AI-684 default-decline posture).
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"session/request_permission","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        var error = doc.RootElement.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Cancelling_token_abandons_pending_request_without_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        using var requestCts = new CancellationTokenSource();
        var       requestTask = harness.Connection.RequestAsync("session/prompt", null, requestCts.Token);

        // Make sure the request was actually sent before cancelling, so we're testing abandonment
        // of an in-flight call, not a call that never started.
        await harness.ReadFrameFromConnectionAsync();

        requestCts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => requestTask.WaitAsync(HangGuard));

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Malformed_line_is_skipped_and_loop_still_delivers_next_valid_frame() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync("{not valid json at all");
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task NotifyAsync_writes_notification_frame_without_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        await harness.Connection.NotifyAsync("session/cancel", null).WaitAsync(HangGuard);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("session/cancel");
        await Assert.That(doc.RootElement.TryGetProperty("id", out _)).IsFalse();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    static async Task SwallowCancellation(Task task) {
        try {
            await task.WaitAsync(HangGuard);
        } catch (OperationCanceledException) {
            // expected shutdown path for this test's owned CTS
        }
    }
}
