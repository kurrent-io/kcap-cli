using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Serialises hosted-agent terminal output onto a single ordered channel so the
/// stream the web "Terminal" tab renders is delivered in PTY order and survives a
/// flapping SignalR connection <em>without losing bytes</em> (AI-842, AI-844).
///
/// The pre-AI-842 path fired every PTY chunk at <c>HubConnection.SendAsync</c>
/// with a discarded task. While the hub was disconnected those sends faulted
/// silently — dropping bytes mid-ANSI-escape-sequence — and the read loop's
/// concurrent fire-and-forget sends could interleave out of order. A single
/// consumer draining one channel fixes the ordering and the silent-fault problem.
///
/// AI-842 capped that channel with <c>DropOldest</c>, which re-introduced silent
/// loss: under back-pressure (a slow/flapping tunnel) it discarded the oldest
/// queued chunks. Claude Code's TUI is a cursor-addressing redraw stream — a
/// single dropped or reordered chunk desyncs every later repaint, so the
/// "Terminal" tab rendered garbled output with lost history (AI-844). The queue
/// is therefore loss-free:
/// <list type="bullet">
///   <item>The bounded channel uses <see cref="BoundedChannelFullMode.Wait"/>, so
///   a full queue back-pressures the producer (<see cref="EnqueueAsync"/> awaits)
///   instead of dropping. The PTY read loop awaits the enqueue, so a stalled
///   transport naturally back-pressures the PTY (and thus the agent) rather than
///   corrupting the mirror; memory stays bounded by <c>capacity</c>.</item>
///   <item>A send that fails <em>while the transport is down</em> is held and
///   retried with the same chunk (head-of-line), so a reconnect outage preserves
///   PTY order and loses nothing.</item>
///   <item>A send that fails <em>while the hub reports Connected</em> is bounded-
///   retried up to <c>maxConnectedAttempts</c>; only if it still won't land — an
///   anomaly that blind retry can't fix and that would otherwise wedge the single
///   shared loop — is that one chunk dropped, counted (<see cref="DroppedChunks"/>),
///   and logged at Warning. This is the sole remaining drop path, and it is never
///   silent.</item>
/// </list>
/// Cancellation (daemon shutdown) ends the loop even while a chunk is being held.
/// </summary>
internal sealed partial class TerminalOutputSender {
    readonly Channel<(string AgentId, string Base64Data)> _channel;
    readonly Func<string, string, CancellationToken, Task> _send;
    readonly Func<bool>                                   _isConnected;
    readonly ILogger                                       _logger;
    readonly TimeSpan                                      _retryDelay;
    readonly int                                           _maxConnectedAttempts;
    long                                                   _dropped;

    public TerminalOutputSender(
            Func<string, string, CancellationToken, Task> send,
            Func<bool>                                    isConnected,
            ILogger                                       logger,
            int                                           capacity             = 2000,
            TimeSpan?                                     retryDelay           = null,
            int                                           maxConnectedAttempts = 5
        ) {
        _send                 = send;
        _isConnected          = isConnected;
        _logger               = logger;
        _retryDelay           = retryDelay ?? TimeSpan.FromMilliseconds(500);
        _maxConnectedAttempts = Math.Max(1, maxConnectedAttempts);
        _channel = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(capacity) {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true
            }
        );
    }

    /// <summary>Total chunks dropped because a connected send kept failing past the retry budget.</summary>
    public long DroppedChunks => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Queues a base64 PTY chunk for in-order delivery, awaiting if the queue is
    /// full so the producer is back-pressured rather than any chunk being dropped.
    /// Returns once the chunk is queued (or silently on shutdown, when the channel
    /// is completed or <paramref name="ct"/> is cancelled — nothing left to deliver).
    /// </summary>
    public async ValueTask EnqueueAsync(string agentId, string base64Data, CancellationToken ct = default) {
        try {
            await _channel.Writer.WriteAsync((agentId, base64Data), ct);
        } catch (OperationCanceledException) {
            // Daemon shutdown / agent stop — drop the in-flight enqueue silently.
        } catch (ChannelClosedException) {
            // Complete() was called as part of teardown — nothing more to deliver.
        }
    }

    /// <summary>Signals no more chunks will be queued so <see cref="RunAsync"/> can drain and exit.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Drains the channel, sending each chunk in order. See the type remarks for the
    /// hold-and-retry (transport down) vs. bounded-retry-then-counted-drop
    /// (connected) policies. Cancellation ends the loop even while holding a chunk.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        try {
            await foreach (var (agentId, base64) in _channel.Reader.ReadAllAsync(ct)) {
                var connectedAttempts = 0;

                while (true) {
                    try {
                        await _send(agentId, base64, ct);

                        break;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        if (_isConnected()) {
                            // Connected yet the send threw — not a transport outage.
                            // Bounded-retry so a transient blip still lands in order;
                            // give up (drop + count + log) once the budget is spent so
                            // one stuck chunk can't wedge the loop (and DisposeAsync).
                            if (++connectedAttempts >= _maxConnectedAttempts) {
                                var total = Interlocked.Increment(ref _dropped);
                                LogSendDropped(ex, agentId, connectedAttempts, total);

                                break;
                            }

                            LogSendRetryConnected(ex, agentId, connectedAttempts, _maxConnectedAttempts);
                        } else {
                            // Transport down: hold this chunk and retry it indefinitely
                            // (the channel back-pressures the producer, so this can't
                            // grow memory without bound). Reset the connected budget —
                            // it only counts consecutive failures while Connected.
                            connectedAttempts = 0;
                            LogSendRetry(ex, agentId, _retryDelay.TotalSeconds);
                        }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal output send for agent {AgentId} failed while transport down, holding and retrying in {Delay}s")]
    partial void LogSendRetry(Exception ex, string agentId, double delay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Terminal output send for agent {AgentId} failed while connected (attempt {Attempt}/{Max}), retrying")]
    partial void LogSendRetryConnected(Exception ex, string agentId, int attempt, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal output send for agent {AgentId} still failing while connected after {Attempts} attempts — dropping chunk (total dropped this session: {TotalDropped})")]
    partial void LogSendDropped(Exception ex, string agentId, int attempts, long totalDropped);
}
