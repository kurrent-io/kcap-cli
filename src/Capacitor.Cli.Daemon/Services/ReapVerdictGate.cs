using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The single reap slot of a hosted runtime. Claiming the slot, snapshotting the launch window, starting
/// the reap and publishing the verdict happen under one lock, and the gated status send takes the same
/// lock — so a non-failure status is either initiated before the verdict exists or not at all.
/// </summary>
/// <param name="insideLaunchWindow">Read under the lock, before the starter runs: the reap's own
/// termination can close the window, so reading it afterwards would always see it closed.</param>
internal sealed class ReapVerdictGate(Func<bool> insideLaunchWindow, ILogger logger) {
    readonly Lock _lock = new();
    bool  _claimed;
    Task? _reapTask;

    public TerminationVerdict? Verdict { get; private set; }

    internal Action? BeforeReadVerdictLockForTest;
    internal Action? BeforeGatedSendHookForTest;

    /// <summary>The verdict, read under the lock, so a reader racing a claim never sees the slot claimed
    /// but the verdict not yet assigned.</summary>
    internal TerminationVerdict? ReadVerdict() {
        // A test hook must never be able to break a production read.
        try { BeforeReadVerdictLockForTest?.Invoke(); }
        catch (Exception ex) { logger.LogDebug(ex, "BeforeReadVerdictLockForTest threw; reading the verdict anyway."); }

        lock (_lock) return Verdict;
    }

    internal bool TryInitiateNonFailureStatusSend(Func<Task> send, out Task sendTask) {
        lock (_lock) {
            if (Verdict is { ReapedInsideLaunchWindow: true }) {
                sendTask = Task.CompletedTask;
                return false;
            }

            BeforeGatedSendHookForTest?.Invoke();
            sendTask = send();
            return true;
        }
    }

    /// <summary><paramref name="start"/> runs under the lock and must return without awaiting the
    /// termination it begins.</summary>
    internal bool TryStartReap(string reason, Func<Task> start) {
        lock (_lock) {
            if (_claimed) return false;

            _claimed = true;
            var inside = insideLaunchWindow();

            try {
                _reapTask = start();
            } catch {
                _claimed = false;
                throw;
            }

            Verdict = new TerminationVerdict(SanitizeReason(reason), inside);
            return true;
        }
    }

    internal Task? TakeReap() { lock (_lock) return _reapTask; }

    static string SanitizeReason(string reason) => reason.ReplaceLineEndings(" ").Trim();
}
