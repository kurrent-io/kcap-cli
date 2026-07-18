using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B (D4 §6.4): the shared "reap by exact identity" primitive used by the
/// <c>HandleStopAgent</c> PID-record fallback (T7), the kill-quarantine retry (T8), and the startup
/// <c>OrphanReaper</c> (T9). Kills a leftover child ONLY when its identity is proven — never on
/// ambiguity — and reports whether death is CONFIRMED (so the caller deletes the record / drains the
/// quarantine only then, per spec §6.4(2)).
/// <para><b>Tri-state identity (fail-closed).</b> The leader pid is classified against its expected
/// start token as one of: <c>Dead</c> (pid gone — or a Linux zombie, treated as gone), <c>Ours</c>
/// (alive, exact match), <c>Recycled</c> (alive, CONCLUSIVELY a different incarnation — same scheme,
/// different value), or <c>Ambiguous</c> (alive but token unreadable / foreign scheme). Only <c>Dead</c>
/// and <c>Recycled</c> are "confirmed gone"; <c>Ambiguous</c> SPARES (returns "not gone") so a
/// still-alive child is never dropped on an unreadable token.</para>
/// <para><b>PID-reuse safety.</b> The token is re-classified before EVERY signal and after every wait,
/// so a pid recycled anywhere in the TERM→grace→KILL window is seen as <c>Recycled</c> and no signal is
/// sent to the new occupant.</para>
/// <para><b>Descendant containment.</b> Signals go to the process GROUP (<c>kill(-pid)</c>; a hosted
/// agent is a forkpty session leader with pgid==pid, so its mcp children are in the group) WHILE the
/// live leader still anchors ownership — so a stubborn descendant is SIGKILLed alongside the leader we
/// have proven is ours. Once the leader is gone we do NOT signal the numeric pgid: an empty leader slot
/// no longer proves the group is our lineage (the pid could be reused by an unrelated session leader),
/// and "ambiguity never kills". A descendant that outlives a leader which exited on TERM before the
/// group SIGKILL is the deferred native-containment concern (Linux PDEATHSIG / Windows Job Object),
/// which kills descendants with the leader at the OS level.</para>
/// </summary>
internal static class ProcessReaper {
    static readonly TimeSpan GraceBeforeKill = TimeSpan.FromSeconds(5);

    enum LeaderState { Dead, Ours, Recycled, Ambiguous }

    static LeaderState Classify(int pid, string expectedIdentity) {
        if (!ProcessIdentity.IsAlive(pid)) return LeaderState.Dead; // gone or a zombie (IsAlive is zombie-aware)

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
            case LeaderState.Dead:      return true;   // our process is gone
            case LeaderState.Recycled:  return true;   // pid reused by an unrelated proc — ours is gone
            case LeaderState.Ambiguous:
                logger.LogWarning(
                    "ProcessReaper: identity uncomparable for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
        }

        // Ours (the EXACT start token already proves this is the same incarnation recorded at spawn).
        // Unix env guard, defense-in-depth: only kill if the live process ALSO still carries our
        // KCAP_AGENT_ID. Unreadable OR mismatched env → SPARE and retain the record: it means ownership
        // can't be proven strongly enough to kill, NOT that the process is gone. Reporting confirmed-gone
        // here would strand a live child and drop its durable tracking (esp. on macOS, where the marker
        // scan can't recover it). Only Dead / a conclusive token recycle return confirmed-gone.
        if (!OperatingSystem.IsWindows()) {
            var envAgentId = ProcessIdentity.ReadAgentEnv(pid, "KCAP_AGENT_ID");
            if (envAgentId is null || !string.Equals(envAgentId, record.AgentId, StringComparison.Ordinal)) {
                logger.LogWarning(
                    "ProcessReaper: env unreadable/mismatched for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                    pid, record.AgentId);
                return false;
            }
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
    /// "ours is gone" and never signalled. The group signal (TERM then KILL) is only ever sent while the
    /// leader is alive-and-ours, so a stubborn descendant dies alongside the proven leader; once the
    /// leader is gone we never signal the pgid. Returns true only on confirmed death (or proven recycle),
    /// false on unconfirmed/ambiguous.</summary>
    static async Task<bool> KillConfirmAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct) {
        try {
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: false); // SIGTERM the group (leader is proven ours → group is our lineage)

            for (var waited = TimeSpan.Zero; waited < GraceBeforeKill; waited += TimeSpan.FromMilliseconds(250)) {
                switch (Classify(pid, expectedIdentity)) {
                    case LeaderState.Dead:      return true; // leader (and the group it anchored) gone
                    case LeaderState.Recycled:  return true;
                    case LeaderState.Ambiguous: return false;
                }
                await Task.Delay(250, ct);
            }

            // Grace elapsed. Re-classify immediately before the hard kill — the pid may have been recycled
            // during the last wait, and SIGKILL to a recycled group would hit unrelated processes.
            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      return true;
                case LeaderState.Recycled:  return true;
                case LeaderState.Ambiguous: return false;
            }

            SignalGroup(pid, hard: true); // SIGKILL the group while the leader is still proven ours
            await Task.Delay(250, ct);

            switch (Classify(pid, expectedIdentity)) {
                case LeaderState.Dead:      return true;
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
    /// isn't a group leader). Only ever called with the leader proven alive-and-ours, so the group is our
    /// lineage. On Windows there are no process groups, so the soft call tree-kills and the hard call is a
    /// no-op (the tree is already gone).</summary>
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
}
