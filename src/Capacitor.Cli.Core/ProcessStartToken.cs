using System.Diagnostics;

namespace Capacitor.Cli.Core;

/// <summary>
/// A machine-local token identifying a specific process <i>incarnation</i>,
/// stable across separate reader processes. Stored on the second line of the
/// daemon PID file and compared to tell a live daemon apart from a recycled
/// PID (liveness check, fix).
///
/// <para><b>Why not just <see cref="Process.StartTime"/>?</b> On Linux that
/// value is NOT stable across processes: .NET derives it from a per-call
/// boot-time estimate (wall clock minus uptime), so two processes reading the
/// <i>same</i> PID get tick values that differ by milliseconds-to-seconds and
/// drift as the realtime clock is adjusted (NTP). The daemon wrote its own
/// start time, and the CLI's <c>status</c>/<c>stop</c>/<c>doctor</c> — separate
/// processes — recomputed a slightly different value, so an exact-tick equality
/// check classified every <i>live</i> daemon as a stale PID file.</para>
///
/// <para>On Linux the token is <c>lx:&lt;boot_id&gt;:&lt;starttime&gt;</c> where
/// <c>starttime</c> is field 22 of <c>/proc/&lt;pid&gt;/stat</c> (clock ticks
/// since boot — kernel-stored, never recomputed, byte-identical for every
/// reader) and <c>boot_id</c> is the per-boot UUID from
/// <c>/proc/sys/kernel/random/boot_id</c>. The boot id disambiguates the
/// otherwise boot-relative starttime so a PID file that survives a reboot can't
/// match an unrelated process that happens to reuse the PID and tick offset.
/// On macOS/Windows <see cref="Process.StartTime"/> is an absolute timestamp
/// already identical across processes (and across reboots), so the token is
/// <c>tk:&lt;ticks&gt;</c>.</para>
///
/// <para>The <c>scheme:</c> prefix lets <see cref="Matches"/> distinguish "a
/// different incarnation" (same scheme, different value — conclusive) from "a
/// token I can't compare" (a legacy PID file that stored bare
/// <see cref="Process.StartTime"/> ticks with no prefix, encountered mid-upgrade
/// while the old daemon is still running). The latter returns null so callers
/// fall back to a weaker image-name check instead of stranding a live daemon.</para>
/// </summary>
public static class ProcessStartToken {
    /// <summary>Token for the calling process, or null if it can't be read.</summary>
    public static string? ForCurrent() => ForPid(Environment.ProcessId);

    /// <summary>
    /// Token for <paramref name="pid"/>, or null if the process is gone or the
    /// value can't be read.
    /// </summary>
    public static string? ForPid(int pid) {
        if (OperatingSystem.IsLinux()) {
            var starttime = ReadLinuxStartTicks(pid);

            return starttime is null ? null : $"lx:{LinuxBootId()}:{starttime}";
        }

        try {
            using var process = Process.GetProcessById(pid);

            return $"tk:{process.StartTime.ToUniversalTime().Ticks}";
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Tri-state comparison of <paramref name="expectedToken"/> (from a PID
    /// file) against the live process at <paramref name="pid"/>:
    /// <list type="bullet">
    /// <item><c>true</c> — same scheme, same value: it IS that incarnation.</item>
    /// <item><c>false</c> — same scheme, different value: a different incarnation
    /// (e.g. a recycled PID). Conclusive; do not fall back.</item>
    /// <item><c>null</c> — can't compare (process gone, token unreadable, or
    /// <paramref name="expectedToken"/> is a legacy/foreign scheme). Callers
    /// should use a weaker check rather than treat the PID as not-ours.</item>
    /// </list>
    /// </summary>
    public static bool? Matches(int pid, string expectedToken) {
        var actual = ForPid(pid);

        if (actual is null || !SameScheme(actual, expectedToken)) return null;

        return string.Equals(actual, expectedToken, StringComparison.Ordinal);
    }

    /// <summary>Two tokens share a scheme when their text up to the first ':' matches.</summary>
    static bool SameScheme(string a, string b) {
        var ia = a.IndexOf(':');
        var ib = b.IndexOf(':');

        return ia > 0 && ib > 0 && a.AsSpan(0, ia).SequenceEqual(b.AsSpan(0, ib));
    }

    /// <summary>Per-boot UUID, or "?" if unreadable (consistent across readers within a boot).</summary>
    static string LinuxBootId() {
        try {
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        } catch {
            return "?";
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
            var stat      = File.ReadAllText($"/proc/{pid}/stat");
            var afterComm = stat.LastIndexOf(')');

            if (afterComm < 0 || afterComm + 2 >= stat.Length) return null;

            var fields = stat[(afterComm + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length > 19 ? fields[19] : null;
        } catch {
            return null;
        }
    }
}
