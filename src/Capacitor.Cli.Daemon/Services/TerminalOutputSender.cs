using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Serialises hosted-agent terminal output onto a single ordered channel so the
/// stream the web "Terminal" tab renders is delivered in PTY order and survives a
/// flapping SignalR connection without corruption (AI-842).
///
/// The pre-AI-842 path fired every PTY chunk at <c>HubConnection.SendAsync</c>
/// with a discarded task. While the hub was disconnected those sends faulted
/// silently — dropping bytes mid-ANSI-escape-sequence — and the read loop's
/// concurrent fire-and-forget sends could interleave out of order. A single
/// consumer draining one channel fixes both: chunks go out strictly in the
/// order they were produced, and a send that throws because the transport is
/// down is retried with the SAME chunk (head-of-line, preserving order) until
/// it lands or the daemon shuts down. The channel is bounded with
/// <c>DropOldest</c> so a prolonged outage caps memory rather than growing
/// without bound — mirroring the agent-run event queue in
/// <see cref="ServerConnection"/>.
/// </summary>
internal sealed partial class TerminalOutputSender {
    readonly Channel<(string AgentId, string Base64Data)> _channel;
    readonly Func<string, string, CancellationToken, Task> _send;
    readonly ILogger                                       _logger;
    readonly TimeSpan                                      _retryDelay;

    public TerminalOutputSender(
            Func<string, string, CancellationToken, Task> send,
            ILogger                                        logger,
            int                                            capacity   = 2000,
            TimeSpan?                                      retryDelay = null
        ) {
        _send       = send;
        _logger     = logger;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        _channel = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest }
        );
    }

    /// <summary>Queues a base64 PTY chunk for in-order delivery. Never blocks.</summary>
    public void Enqueue(string agentId, string base64Data)
        => _channel.Writer.TryWrite((agentId, base64Data));

    /// <summary>Signals no more chunks will be queued so <see cref="RunAsync"/> can drain and exit.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Drains the channel, sending each chunk in order. A send that throws is
    /// retried with the same chunk after <c>retryDelay</c> so a transport outage
    /// holds the stream in place rather than dropping bytes; cancellation (daemon
    /// shutdown) ends the loop even while a chunk is being retried.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        try {
            await foreach (var (agentId, base64) in _channel.Reader.ReadAllAsync(ct)) {
                while (true) {
                    try {
                        await _send(agentId, base64, ct);

                        break;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        LogSendRetry(ex, agentId, _retryDelay.TotalSeconds);

                        try {
                            await Task.Delay(_retryDelay, ct);
                        } catch (OperationCanceledException) {
                            return;
                        }
                    }
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Graceful shutdown — channel read cancelled.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal output send for agent {AgentId} failed (transport down?), retrying in {Delay}s")]
    partial void LogSendRetry(Exception ex, string agentId, double delay);
}
