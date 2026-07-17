using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4): the shared "reap by exact identity" primitive used by the
/// <c>HandleStopAgent</c> PID-record fallback (T7), the kill-quarantine retry (T8), and the startup
/// <c>OrphanReaper</c> (T9). Kills a leftover child ONLY when its identity is proven — never on
/// ambiguity — and reports whether death is CONFIRMED (so the caller deletes the record only then, per
/// spec §6.4(2)).
/// <para>PID-reuse safety: the expected start-identity token is re-validated before EVERY signal and
/// after every wait (<see cref="StillTarget"/>). If the target exits and its pid is recycled by an
/// unrelated process at any point in the TERM→grace→KILL sequence, the mismatch is detected and the
/// signal is NOT sent — "ours is gone" is reported instead of killing the new occupant.</para>
/// <para>Two kill regimes (spec §6.4(2b)):</para>
/// <list type="bullet">
/// <item><b>Record regime</b> (<see cref="ReapByRecordAsync"/>): exact <c>(pid, start-identity)</c>
/// match PLUS, on Unix, the <c>KCAP_AGENT_ID</c> env check. If the env can't be read (macOS 26 redacts
/// it — see <see cref="ProcessIdentity.ReadAgentEnv"/>) the process is SPARED.</item>
/// <item><b>Marker regime</b> (<see cref="ReapByMarkerAsync"/>): the caller has already validated the
/// live process's env triple; this captures the process's start-identity and kills by it (so PID reuse
/// during the kill still can't hit the wrong process). If the identity can't be captured, it SPARES.</item>
/// </list>
/// </summary>
internal static class ProcessReaper {
    static readonly TimeSpan GraceBeforeKill = TimeSpan.FromSeconds(5);

    /// <summary>True IFF <paramref name="pid"/> is alive AND (no identity was supplied OR it still
    /// matches <paramref name="expectedIdentity"/>). Returns false when the target is gone OR the pid
    /// has been recycled by a different process — the caller then treats it as "ours is gone" and never
    /// signals.</summary>
    static bool StillTarget(int pid, string? expectedIdentity) =>
        ProcessIdentity.IsAlive(pid) && (expectedIdentity is not { } id || ProcessIdentity.Matches(pid, id));

    /// <summary>Record-regime reap. Returns true when the target is CONFIRMED gone (already dead,
    /// killed + observed dead, or a proven identity mismatch — i.e. our agent's process is not there),
    /// false when it may still be alive (kill unconfirmed, or SPARED for unreadable env).</summary>
    public static async Task<bool> ReapByRecordAsync(AgentPidRecord record, ILogger logger, CancellationToken ct) {
        var pid = record.Pid;

        if (!ProcessIdentity.IsAlive(pid)) return true;                          // gone
        if (!ProcessIdentity.Matches(pid, record.StartIdentity)) return true;    // PID reused by an unrelated proc — ours is gone

        // Unix env guard: only kill if the process still proves it is OUR agent. Unreadable env → spare.
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

    /// <summary>Marker-regime reap of a recordless survivor: the caller has already read + validated the
    /// env triple from the live process. Captures the process's start-identity so the kill sequence is
    /// PID-reuse-safe; SPARES (returns false) if the identity can't be captured. Returns true when
    /// confirmed gone.</summary>
    public static Task<bool> ReapByMarkerAsync(int pid, string agentId, ILogger logger, CancellationToken ct) {
        if (!ProcessIdentity.IsAlive(pid)) return Task.FromResult(true);

        var identity = ProcessIdentity.Capture(pid);
        if (identity is null) {
            logger.LogWarning(
                "ProcessReaper: could not capture identity for pid {Pid} (agent {AgentId}) — sparing (ambiguity never kills)",
                pid, agentId);
            return Task.FromResult(false);
        }

        return KillConfirmAsync(pid, identity, agentId, logger, ct);
    }

    /// <summary>Kill-quarantine retry (spec §6.4(2a)): the entry is a KNOWN-ours process whose death
    /// wasn't confirmed at teardown. Kill it by EXACT identity (no env check needed — we already own it,
    /// and the identity match handles PID reuse) and report confirmed-gone. Gone / PID-reused → true
    /// (drain it); still alive after SIGKILL → false (retry next tick).</summary>
    public static Task<bool> ReapByIdentityAsync(int pid, string expectedIdentity, string agentId, ILogger logger, CancellationToken ct) {
        if (!StillTarget(pid, expectedIdentity)) return Task.FromResult(true); // gone / reused → ours is gone
        return KillConfirmAsync(pid, expectedIdentity, agentId, logger, ct);
    }

    /// <summary>SIGTERM the process group (Unix; the forkpty child is a session leader so its pgid ==
    /// pid, taking descendants too), wait <see cref="GraceBeforeKill"/>, then SIGKILL; confirm death.
    /// The <paramref name="expectedIdentity"/> is re-validated before EACH signal and after each wait,
    /// so a pid recycled mid-sequence is detected as "ours is gone" and never signalled. Windows falls
    /// back to a best-effort <see cref="System.Diagnostics.Process"/> kill.</summary>
    static async Task<bool> KillConfirmAsync(int pid, string? expectedIdentity, string agentId, ILogger logger, CancellationToken ct) {
        try {
            if (!StillTarget(pid, expectedIdentity)) return true; // exited / recycled before we signal

            if (OperatingSystem.IsWindows()) {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* gone / unkillable */ }
            } else {
                // Negative pid → the whole process group (killpg equivalent). Fall back to the pid alone.
                if (UnixPtyInterop.kill(-pid, UnixPtyInterop.SIGTERM) != 0)
                    UnixPtyInterop.kill(pid, UnixPtyInterop.SIGTERM);
            }

            for (var waited = TimeSpan.Zero; waited < GraceBeforeKill; waited += TimeSpan.FromMilliseconds(250)) {
                if (!StillTarget(pid, expectedIdentity)) return true;
                await Task.Delay(250, ct);
            }

            // Re-validate AFTER the grace wait, immediately before the hard kill — the pid may have been
            // recycled during the 5s window, and SIGKILL to a recycled pid would hit an unrelated process.
            if (!StillTarget(pid, expectedIdentity)) return true;

            if (!OperatingSystem.IsWindows()) {
                if (UnixPtyInterop.kill(-pid, UnixPtyInterop.SIGKILL) != 0)
                    UnixPtyInterop.kill(pid, UnixPtyInterop.SIGKILL);
                await Task.Delay(250, ct);
            }

            var gone = !StillTarget(pid, expectedIdentity);
            if (!gone) logger.LogWarning("ProcessReaper: pid {Pid} (agent {AgentId}) still alive after SIGKILL — retained for next sweep", pid, agentId);

            return gone;
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            logger.LogWarning(ex, "ProcessReaper: kill of pid {Pid} (agent {AgentId}) failed", pid, agentId);
            return false;
        }
    }
}
