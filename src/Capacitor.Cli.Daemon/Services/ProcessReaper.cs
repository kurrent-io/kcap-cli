using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4): the shared "reap by exact identity" primitive used by the
/// <c>HandleStopAgent</c> PID-record fallback (T7), the kill-quarantine retry (T8), and the startup
/// <c>OrphanReaper</c> (T9). Kills a leftover child ONLY when its identity is proven — never on
/// ambiguity — and reports whether death is CONFIRMED (so the caller deletes the record / drains the
/// quarantine only then, per spec §6.4(2)).
/// <para><b>Tri-state identity (fail-closed).</b> The leader pid is classified against its expected
/// start token as one of: <c>Dead</c> (pid gone), <c>Ours</c> (alive, exact match), <c>Recycled</c>
/// (alive, CONCLUSIVELY a different incarnation — same scheme, different value), or <c>Ambiguous</c>
/// (alive but token unreadable / foreign scheme). Only <c>Dead</c> and <c>Recycled</c> are "confirmed
/// gone"; <c>Ambiguous</c> SPARES (returns "not gone") so a still-alive child is never dropped on an
/// unreadable token.</para>
/// <para><b>PID-reuse safety.</b> The token is re-classified before EVERY signal and after every wait,
/// so a pid recycled anywhere in the TERM→grace→KILL window is seen as <c>Recycled</c> and no signal is
/// sent to the new occupant.</para>
/// <para><b>Descendant containment.</b> Signals go to the process GROUP (<c>kill(-pid)</c>; a hosted
/// agent is a forkpty session leader with pgid==pid, so its mcp children are in the group). When the
/// leader dies (on TERM or otherwise) any orphaned descendants that inherited its group are swept with
/// an uncatchable group SIGKILL — but only while the leader pid slot is empty, so a reused pid's group
/// is never touched. (Full group-liveness *confirmation* — distinguishing "group empty" from "not a
/// group leader" — needs the same OS-primitive work as the deferred native-containment sub-phase; the
/// uncatchable group SIGKILL already guarantees descendants are cleared.)</para>
/// </summary>
internal static class ProcessReaper {
    static readonly TimeSpan GraceBeforeKill = TimeSpan.FromSeconds(5);

    enum LeaderState { Dead, Ours, Recycled, Ambiguous }

    static LeaderState Classify(int pid, string expectedIdentity) {
        if (!ProcessIdentity.IsAlive(pid)) return LeaderState.Dead;

        return ProcessIdentity.MatchesTri(pid, expectedIdentity) switch {
            true  => LeaderState.Ours,
            false => LeaderState.Recycled,   // same scheme, different value → conclusively a different process
            _     => LeaderState.Ambiguous,  // unreadable / foreign scheme → can't prove → spare
        };
    }

    /// <summary>Record-regime reap. Returns true when the target is CONFIRMED gone (dead, killed +
    /// observed dead, or a proven identity mismatch — our agent's process is not there), false when it
    /// may still be alive (kill unconfirmed, SPARED for an unreadable env, or an uncomparable token).</summary>
    public static async Task<bool> ReapByRecordAsync(AgentPidRecord record, ILogger logger, CancellationToken ct) {
        var pid = record.Pid;

        switch (Classify(pid, record.StartIdentity)) {
            case LeaderState.Dead:      SweepDeadLeaderGroup(pid); return true; // gone — sweep any orphaned descendants
            case LeaderState.Recycled:  return true;                            // pid reused by an unrelated proc — ours is gone
            case LeaderState.Ambiguous:
                logger.LogWarning(
                    "ProcessReaper: identity uncomparable for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
        }

        // Ours. Unix env guard: only kill if the process still proves it is OUR agent. Unreadable → spare.
        if (!OperatingSystem.IsWindows()) {
            var envAgentId = ProcessIdentity.ReadAgentEnv(pid, "KCAP_AGENT_ID");
            if (envAgentId is null) {
                logger.LogWarning(
                    "ProcessReaper: env unreadable for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
            }
            if (!string.Equals(envAgentId, record.AgentId, StringComparison.Ordinal)) return true; // not our agent
        }

        return await KillConfirmAsync(pid, record.StartIdentity, record.AgentId, logger, ct);
    }

    /// <summary>Marker-regime reap of a recordless survivor. The caller (OrphanReaper) has already
    /// captured the process's start token BEFORE reading its env-marker triple and re-validated it after,
    /// so <paramref name="expectedIdentity"/> is bound to the same incarnation whose markers proved
    /// ownership — no recapture here (which could adopt a replacement after a mid-scan PID reuse).</summary>
    public static Task<bool> ReapByMarkerAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct)
        => KillConfirmAsync(pid, expectedIdentity, agentId, logger, ct);

    /// <summary>Kill-quarantine retry (spec §6.4(2a)): the entry is a KNOWN-ours process whose death
    /// wasn't confirmed at teardown. Kill it by EXACT identity and report confirmed-gone (drain) vs
    /// still-alive/uncomparable (retain for the next tick).</summary>
    public static Task<bool> ReapByIdentityAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct)
        => KillConfirmAsync(pid, expectedIdentity, agentId, logger, ct);

    /// <summary>SIGTERM the process group, wait <see cref="GraceBeforeKill"/>, then SIGKILL; confirm the
    /// leader is gone. Re-classifies before/after each step so a pid recycled mid-sequence is detected as
    /// "ours is gone" and never signalled; a leader that dies at any point has its orphaned descendants
    /// swept. Returns true only on confirmed death (or proven recycle), false on unconfirmed/ambiguous.</summary>
    static async Task<bool> KillConfirmAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct) {
        try {
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      SweepDeadLeaderGroup(pid); return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: false); // SIGTERM the group

            for (var waited = TimeSpan.Zero; waited < GraceBeforeKill; waited += TimeSpan.FromMilliseconds(250)) {
                switch (Classify(pid, expectedIdentity)) {
                    case LeaderState.Dead:      SweepDeadLeaderGroup(pid); return true; // died on TERM → sweep descendants
                    case LeaderState.Recycled:  return true;
                    case LeaderState.Ambiguous: return false;
                }
                await Task.Delay(250, ct);
            }

            // Grace elapsed. Re-classify immediately before the hard kill — the pid may have been recycled
            // during the last wait, and SIGKILL to a recycled group would hit unrelated processes.
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      SweepDeadLeaderGroup(pid); return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: true); // SIGKILL the group
            await Task.Delay(250, ct);

            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      SweepDeadLeaderGroup(pid); return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false; // became uncomparable → unconfirmed, retain
                default:
                    logger.LogWarning("ProcessReaper: pid {Pid} (agent {AgentId}) still alive after SIGKILL — retained for next sweep", pid, agentId);
                    return false;
            }
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            logger.LogWarning(ex, "ProcessReaper: kill of pid {Pid} (agent {AgentId}) failed", pid, agentId);
            return false;
        }
    }

    /// <summary>SIGTERM/SIGKILL the target's process group (Unix; falls back to the bare pid when it
    /// isn't a group leader). On Windows there are no process groups, so the soft call tree-kills and the
    /// hard call is a no-op (the tree is already gone).</summary>
    static void SignalGroup(int pid, bool hard) {
        if (pid <= 0) return;

        if (OperatingSystem.IsWindows()) {
            if (!hard) {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* gone / unkillable */ }
            }

            return;
        }

        var sig = hard ? UnixPtyInterop.SIGKILL : UnixPtyInterop.SIGTERM;
        if (UnixPtyInterop.kill(-pid, sig) != 0) UnixPtyInterop.kill(pid, sig); // -pid = the group; fall back to the pid
    }

    /// <summary>Our leader is confirmed gone → SIGKILL any orphaned descendants that inherited its
    /// process group. Reuse-safe: only fires while the leader pid slot is EMPTY (a reused pid would be
    /// alive → skipped), so it can only reach our dead leader's lineage, never a new process's group.
    /// Best-effort; SIGKILL is uncatchable, so the group is cleared. Unix-only (Windows tree-kill already
    /// swept the descendants).</summary>
    static void SweepDeadLeaderGroup(int pid) {
        if (pid <= 0 || OperatingSystem.IsWindows()) return;
        if (ProcessIdentity.IsAlive(pid)) return; // pid slot occupied — never signal a possibly-reused leader's group

        UnixPtyInterop.kill(-pid, UnixPtyInterop.SIGKILL);
    }
}
