using System.Diagnostics;

namespace Capacitor.Cli.Core;

/// <summary>
/// A machine-local token identifying a specific process <i>incarnation</i>,
/// stable across separate reader processes. Stored on the second line of the
/// daemon PID file and compared to tell a live daemon apart from a recycled
/// PID (AI-630 liveness check, AI-839 fix).
///
/// <para><b>Why not just <see cref="Process.StartTime"/>?</b> On Linux that
/// value is NOT stable across processes: .NET derives it from a per-call
/// boot-time estimate (wall clock minus uptime), so two processes reading the
/// <i>same</i> PID get tick values that differ by milliseconds-to-seconds and
/// drift as the realtime clock is adjusted (NTP). The daemon wrote its own
/// start time, and the CLI's <c>status</c>/<c>stop</c>/<c>doctor</c> — separate
/// processes — recomputed a slightly different value, so an exact-tick equality
/// check classified every <i>live</i> daemon as a stale PID file (AI-839). The
/// CLI then refused to manage it: <c>status</c> reported "stale", <c>stop</c>
/// no-op'd, and <c>start</c> re-spawned only to collide on the flock.</para>
///
/// <para>On Linux we instead read <c>/proc/&lt;pid&gt;/stat</c> field 22
/// (<c>starttime</c>, in clock ticks since boot). The kernel stores this once
/// at process creation and never recomputes it, so it is byte-identical for
/// every reader and immune to wall-clock adjustments. On macOS and Windows
/// <see cref="Process.StartTime"/> is an absolute timestamp that already <i>is</i>
/// identical across processes, so we keep using its UTC ticks there.</para>
/// </summary>
public static class ProcessStartToken {
    /// <summary>Token for the calling process, or null if it can't be read.</summary>
    public static string? ForCurrent() => ForPid(Environment.ProcessId);

    /// <summary>
    /// Token for <paramref name="pid"/>, or null if the process is gone or the
    /// value can't be read. A null result means callers should fall back to a
    /// weaker identity check (e.g. process image name) rather than treat the
    /// PID as definitely-not-ours.
    /// </summary>
    public static string? ForPid(int pid) {
        if (OperatingSystem.IsLinux()) return ReadLinuxStartTicks(pid);

        try {
            using var process = Process.GetProcessById(pid);

            return process.StartTime.ToUniversalTime().Ticks.ToString();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads field 22 (<c>starttime</c>) from <c>/proc/&lt;pid&gt;/stat</c>. The
    /// line is <c>pid (comm) state ppid ...</c> where <c>comm</c> can itself
    /// contain spaces and <c>)</c>, so we split on the text after the LAST
    /// <c>)</c>: those tokens begin at field 3 (<c>state</c>), making
    /// <c>starttime</c> index 19.
    /// </summary>
    static string? ReadLinuxStartTicks(int pid) {
        try {
            var stat       = File.ReadAllText($"/proc/{pid}/stat");
            var afterComm  = stat.LastIndexOf(')');

            if (afterComm < 0 || afterComm + 2 >= stat.Length) return null;

            var fields = stat[(afterComm + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length > 19 ? fields[19] : null;
        } catch {
            return null;
        }
    }
}
