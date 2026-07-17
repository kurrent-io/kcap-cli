using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4): the shared "reap by exact identity" primitive used by the
/// <c>HandleStopAgent</c> PID-record fallback (T7) and the startup <c>OrphanReaper</c> (T9). Kills a
/// leftover child ONLY when its identity is proven — never on ambiguity — and reports whether death is
/// CONFIRMED (so the caller deletes the record only then, per spec §6.4(2)).
/// <para>Two kill regimes (spec §6.4(2b)):</para>
/// <list type="bullet">
/// <item><b>Record regime</b> (<see cref="ReapByRecordAsync"/>): exact <c>(pid, start-identity)</c>
/// match PLUS, on Unix, the <c>KCAP_AGENT_ID</c> env check. If the env can't be read (macOS 26 redacts
/// it — see <see cref="ProcessIdentity.ReadAgentEnv"/>) the process is SPARED.</item>
/// <item><b>Marker regime</b> (<see cref="ReapByMarkerAsync"/>): the full env triple read from the live
/// process itself (<c>KCAP_AGENT_ID</c> present, <c>KCAP_DAEMON_ID</c> == ours, <c>KCAP_DAEMON_EPOCH</c>
/// from a prior incarnation). IS full identity for a recordless process; no native stamp needed.</item>
/// </list>
/// </summary>
internal static class ProcessReaper {
    static readonly TimeSpan GraceBeforeKill = TimeSpan.FromSeconds(5);

    /// <summary>Record-regime reap. Returns true when the target is CONFIRMED gone (already dead,
    /// killed + observed dead, or a proven identity mismatch — i.e. our agent's process is not there),
    /// false when it may still be alive (kill unconfirmed, or SPARED for unreadable env).</summary>
    public static async Task<bool> ReapByRecordAsync(AgentPidRecord record, ILogger logger, CancellationToken ct) {
        var pid = record.Pid;

        if (!ProcessIdentity.IsAlive(pid)) return true;                    // gone
        if (!ProcessIdentity.Matches(pid, record.StartIdentity)) return true; // PID reused by an unrelated proc — ours is gone

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

        return await KillConfirmAsync(pid, record.AgentId, logger, ct);
    }

    /// <summary>Marker-regime reap of a recordless survivor: the caller has already read + validated the
    /// env triple from the live process. Returns true when confirmed gone.</summary>
    public static Task<bool> ReapByMarkerAsync(int pid, string agentId, ILogger logger, CancellationToken ct) {
        if (!ProcessIdentity.IsAlive(pid)) return Task.FromResult(true);
        return KillConfirmAsync(pid, agentId, logger, ct);
    }

    /// <summary>SIGTERM the process group (Unix; the forkpty child is a session leader so its pgid ==
    /// pid, taking descendants too), wait <see cref="GraceBeforeKill"/>, then SIGKILL; confirm death.
    /// Windows falls back to a best-effort <see cref="System.Diagnostics.Process"/> kill.</summary>
    static async Task<bool> KillConfirmAsync(int pid, string agentId, ILogger logger, CancellationToken ct) {
        try {
            if (OperatingSystem.IsWindows()) {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* gone / unkillable */ }
            } else {
                // Negative pid → the whole process group (killpg equivalent). Fall back to the pid alone.
                if (UnixPtyInterop.kill(-pid, UnixPtyInterop.SIGTERM) != 0)
                    UnixPtyInterop.kill(pid, UnixPtyInterop.SIGTERM);
            }

            for (var waited = TimeSpan.Zero; waited < GraceBeforeKill; waited += TimeSpan.FromMilliseconds(250)) {
                if (!ProcessIdentity.IsAlive(pid)) return true;
                await Task.Delay(250, ct);
            }

            if (!OperatingSystem.IsWindows()) {
                if (UnixPtyInterop.kill(-pid, UnixPtyInterop.SIGKILL) != 0)
                    UnixPtyInterop.kill(pid, UnixPtyInterop.SIGKILL);
                await Task.Delay(250, ct);
            }

            var gone = !ProcessIdentity.IsAlive(pid);
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
