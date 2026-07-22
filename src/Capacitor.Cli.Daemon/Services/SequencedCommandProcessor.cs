using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal enum SequencedKind { Launch, Stop }

internal readonly record struct SequencedItem(
    SequencedKind Kind, string Epoch, long Seq, string CommandId, string AgentId);

internal readonly record struct CommandOutcome(
    CommandOutcomeKind Kind, string? AgentId = null, string? SessionId = null, CommandRejectedReason? RejectReason = null);

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.2; parent §5.5): the daemon's two-lane sequenced
/// command handler. Exactly two command types are sequenced (Seq'd LaunchAgentCommand + StopAgentV2),
/// executed strictly serially per epoch. Acceptance (bump HighestAcceptedSeq + cache entry + enqueue)
/// is one atomic operation under <c>_lock</c>; LastProcessedSeq is the contiguous terminal prefix
/// (advances only on a terminal outcome). Self-contained + delegate-injected so it is unit-testable
/// with no live orchestrator (mirrors OrphanReaper/AgentKillQuarantine).
/// </summary>
internal sealed class SequencedCommandProcessor : IAsyncDisposable {
    sealed class CacheEntry { public required string CommandId; public bool Processed; public CommandOutcome Outcome; }
    readonly record struct LaneItem(SequencedItem Item, Func<Task<CommandOutcome>> Execute, TaskCompletionSource Done);

    readonly string _epoch;
    readonly Func<string, AgentLiveness> _readLiveness;
    readonly Func<CommandAck, Task> _sendAck;
    readonly Func<CommandRejected, Task> _sendRejected;
    readonly ILogger _logger;
    readonly int _cacheBound;

    readonly object _lock = new();
    long _highestAcceptedSeq;
    long _lastProcessedSeq;
    long _lastAckedPrefix;
    readonly Dictionary<long, CacheEntry> _cache = new();
    readonly Channel<LaneItem> _lane = Channel.CreateUnbounded<LaneItem>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _laneTask;

    public SequencedCommandProcessor(
            string epoch, Func<string, AgentLiveness> readLiveness,
            Func<CommandAck, Task> sendAck, Func<CommandRejected, Task> sendRejected,
            ILogger logger, int cacheBound = 256) {
        _epoch = epoch; _readLiveness = readLiveness; _sendAck = sendAck; _sendRejected = sendRejected;
        _logger = logger; _cacheBound = cacheBound;
        _laneTask = Task.Run(RunLaneAsync);
    }

    public string Epoch => _epoch;
    public long HighestAcceptedSeq { get { lock (_lock) return _highestAcceptedSeq; } }
    public long LastProcessedSeq   { get { lock (_lock) return _lastProcessedSeq; } }

    public Task SubmitAsync(SequencedItem item, Func<Task<CommandOutcome>> execute) {
        lock (_lock) {
            if (!string.Equals(item.Epoch, _epoch, StringComparison.Ordinal))
                return RejectLocked(item, CommandRejectedReason.StaleEpoch);   // never touches THIS epoch's lane

            if (_cache.TryGetValue(item.Seq, out var existing))
                return HandleDuplicateLocked(item, existing);                   // Task 13 answers with CommandAck

            if (item.Seq != _highestAcceptedSeq + 1)
                return HandleNonNextLocked(item);                              // Task 15 sends wrong_next

            // ACCEPT + lane-item, atomically under _lock.
            _highestAcceptedSeq = item.Seq;
            _cache[item.Seq] = new CacheEntry { CommandId = item.CommandId, Processed = false };
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_lane.Writer.TryWrite(new LaneItem(item, execute, done))) {
                SynthesizeErrorLocked(item); // shutdown/allocation race: watermark must still advance
                done.SetResult();
            }
            return done.Task;
        }
    }

    // Task 12 stubs (replaced in later tasks):
    Task HandleDuplicateLocked(SequencedItem item, CacheEntry existing) => Task.CompletedTask;
    Task HandleNonNextLocked(SequencedItem item) => Task.CompletedTask;

    Task RejectLocked(SequencedItem item, CommandRejectedReason reason) {
        _ = _sendRejected(new CommandRejected(item.Epoch, item.Seq, item.CommandId, reason, item.AgentId));
        return Task.CompletedTask;
    }

    void SynthesizeErrorLocked(SequencedItem item) {
        // Lane-item creation failed AFTER acceptance (shutdown/allocation race) — an advertised-accepted
        // Seq with no processable item is impossible, so mark this Seq terminally errored and advance the
        // watermark THROUGH THE CONTIGUOUS PREFIX only. NEVER set _lastProcessedSeq = item.Seq directly:
        // if the lane is completing while an earlier accepted item is still draining, a direct jump to N
        // would (a) advertise a non-contiguous prefix and (b) be regressed below when the earlier item's
        // consumer later advances to N-1. AdvanceWatermarkLocked is monotonic + contiguous by construction.
        _cache[item.Seq] = new CacheEntry {
            CommandId = item.CommandId, Processed = true,
            Outcome = new CommandOutcome(CommandOutcomeKind.InternalError, item.AgentId) };
        AdvanceWatermarkLocked();
        _ = _sendRejected(new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.InternalError, item.AgentId));
    }

    /// <summary>The watermark is the contiguous terminal-processed prefix. Walk forward through Processed
    /// cache entries from _lastProcessedSeq+1 so a synthesized out-of-order terminal (a shutdown race)
    /// never advances past a still-draining earlier item, and no advance can ever regress the watermark
    /// (monotonic by construction). Retired seqs are always &lt;= _lastProcessedSeq, so the walk is safe.</summary>
    void AdvanceWatermarkLocked() {
        while (_cache.TryGetValue(_lastProcessedSeq + 1, out var next) && next.Processed)
            _lastProcessedSeq++;
    }

    /// <summary>Test seam: complete the lane writer so a subsequent accepted Submit's TryWrite fails,
    /// forcing the SynthesizeErrorLocked path deterministically (mirrors a shutdown race).</summary>
    internal void CompleteLaneForTest() => _lane.Writer.TryComplete();

    async Task RunLaneAsync() {
        await foreach (var li in _lane.Reader.ReadAllAsync()) {
            CommandOutcome outcome;
            try {
                outcome = await li.Execute();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "SequencedCommandProcessor: execution faulted for seq {Seq} — internal_error", li.Item.Seq);
                outcome = new CommandOutcome(CommandOutcomeKind.InternalError, li.Item.AgentId);
                _ = _sendRejected(new CommandRejected(li.Item.Epoch, li.Item.Seq, li.Item.CommandId, CommandRejectedReason.InternalError, li.Item.AgentId));
            }

            // Task 15: an execution-time terminal rejection (daemon_capacity / semantic) emits CommandRejected.
            if (outcome.Kind == CommandOutcomeKind.LaunchRejected && outcome.RejectReason is { } r)
                _ = _sendRejected(new CommandRejected(li.Item.Epoch, li.Item.Seq, li.Item.CommandId, r, li.Item.AgentId));

            lock (_lock) {
                if (_cache.TryGetValue(li.Item.Seq, out var e)) { e.Processed = true; e.Outcome = outcome; }
                AdvanceWatermarkLocked(); // contiguous terminal prefix — serial lane => normally == prior + 1,
                                          // but shared with SynthesizeErrorLocked so a race can never regress it
            }
            li.Done.SetResult();
        }
    }

    public void AckPrefix(AckProcessedPrefix ack) { /* Task 14 */ }

    public async ValueTask DisposeAsync() {
        _lane.Writer.TryComplete();
        try { await _laneTask; } catch { /* best-effort */ }
    }
}
