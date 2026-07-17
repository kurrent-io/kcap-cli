using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4(2)): exact process start-identity, liveness, and agent-env read for the
/// PID-record + env-marker reap. The start-identity REUSES the AI-839 <see cref="ProcessStartToken"/>
/// (Linux kernel <c>starttime</c> + <c>boot_id</c>; macOS/Windows absolute <c>StartTime.Ticks</c>) —
/// exact-equality with NO tolerance window, cross-reader-stable, and PID-reuse-safe — so the spec's
/// "no fuzzy DateTime comparison" requirement is met by construction. A gone/recycled/uncomparable
/// process NEVER matches, so an identity mismatch can never authorize a kill.
/// </summary>
internal static partial class ProcessIdentity {
    /// <summary>The exact start-identity token for <paramref name="pid"/>, or null if the process is
    /// gone or the value can't be read. A non-positive pid (0 = a degenerate/Noop runtime; negative =
    /// never a real child) is not a reapable process → null.</summary>
    public static string? Capture(int pid) => pid > 0 ? ProcessStartToken.ForPid(pid) : null;

    /// <summary>True IFF a live process at <paramref name="pid"/> has EXACTLY
    /// <paramref name="expectedIdentity"/> (AI-839 tri-state == true). A gone pid, a recycled pid
    /// (different incarnation), an uncomparable token, or a non-positive pid all return false —
    /// ambiguity never kills.</summary>
    public static bool Matches(int pid, string expectedIdentity) =>
        pid > 0 && ProcessStartToken.Matches(pid, expectedIdentity) == true;

    /// <summary>The AI-839 TRI-STATE identity comparison, preserved for reap decisions that must tell
    /// "conclusively a different incarnation (recycled pid) — safe to treat as gone" (<c>false</c>) apart
    /// from "can't compare — token unreadable or foreign scheme, must SPARE and retain" (<c>null</c>).
    /// A non-positive pid is uncomparable → <c>null</c>. Collapsing <c>null</c> to <c>false</c> (as
    /// <see cref="Matches"/> does) would drop a still-alive child's record on an unreadable token, so
    /// callers that delete records / drain quarantine must use THIS, not <see cref="Matches"/>.</summary>
    public static bool? MatchesTri(int pid, string expectedIdentity) =>
        pid > 0 ? ProcessStartToken.Matches(pid, expectedIdentity) : null;

    /// <summary>Best-effort liveness of <paramref name="pid"/> regardless of identity — only used to
    /// decide whether it's worth probing identity/env. Unix: <c>kill(pid, 0) == 0</c> (ESRCH → dead);
    /// Windows: <see cref="Process.GetProcessById(int)"/> succeeds. A non-positive pid returns false:
    /// <c>kill(0/-1/-pgid, 0)</c> targets a process GROUP (or all processes), never a single child, so
    /// it must never be treated as "our agent is alive".</summary>
    public static bool IsAlive(int pid) {
        if (pid <= 0) return false;

        if (OperatingSystem.IsWindows()) {
            try { using var _ = Process.GetProcessById(pid); return true; }
            catch { return false; }
        }

        if (UnixPtyInterop.kill(pid, 0) != 0) return false; // ESRCH → gone

        // A ZOMBIE (exited but not yet reaped by its parent) still answers kill(pid, 0), but it is
        // effectively dead — its pid holds a slot only until reaped. Treat it as not-alive so a reaper
        // confirms death (rather than seeing a live pid whose token is now unreadable → "ambiguous →
        // spare" → a slot held forever). Linux reads /proc/{pid}/stat state 'Z'. (macOS keeps the
        // kill(0) answer — its parent-reaping and the retry-next-tick cadence clear the window; a prior
        // daemon's orphans reparent to init, which reaps promptly.)
        if (OperatingSystem.IsLinux() && IsLinuxZombie(pid)) return false;

        return true;
    }

    /// <summary>True if <paramref name="pid"/> is a Linux zombie (state field 'Z' in /proc/{pid}/stat).
    /// Best-effort: any read/parse failure returns false (fall back to the kill(0) liveness answer).</summary>
    static bool IsLinuxZombie(int pid) {
        try {
            var stat      = File.ReadAllText($"/proc/{pid}/stat");
            var afterComm = stat.LastIndexOf(')'); // comm can contain spaces/parens; state is the token after the last ')'

            if (afterComm < 0 || afterComm + 2 >= stat.Length) return false;

            var fields = stat[(afterComm + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length > 0 && fields[0].StartsWith('Z');
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Read a single <c>KCAP_*</c> variable from a LIVE process's OWN environment (the record/marker
    /// env check). Linux reads <c>/proc/{pid}/environ</c> (NUL-delimited, same-uid readable); macOS
    /// uses <c>sysctl(KERN_PROCARGS2)</c> (modern macOS restricts <c>ps -E</c> env visibility entirely,
    /// so the spec's <c>ps -E</c> is obsolete — <c>KERN_PROCARGS2</c> is the same-uid mechanism <c>ps</c>
    /// itself used). Returns null when the env can't be read — the caller then SPARES the process
    /// (ambiguity never kills). Windows returns null (no scan path — layer-1 containment covers it).
    /// </summary>
    public static string? ReadAgentEnv(int pid, string key) {
        try {
            if (OperatingSystem.IsLinux()) {
                var raw = File.ReadAllText($"/proc/{pid}/environ");
                foreach (var entry in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    if (entry.StartsWith(key + "=", StringComparison.Ordinal))
                        return entry[(key.Length + 1)..];

                return null;
            }

            if (OperatingSystem.IsMacOS())
                return ReadAgentEnvMacOs(pid, key);

            return null; // Windows: no env scan (layer-1 containment)
        } catch {
            return null;
        }
    }

    // ── macOS KERN_PROCARGS2 env read ────────────────────────────────────────────────────────────

    const int CtlKern       = 1;
    const int KernProcArgs2 = 49;

    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static partial int Sysctl(int[] name, uint namelen, byte[]? oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    /// <summary>Reads <paramref name="pid"/>'s env via <c>sysctl(KERN_PROCARGS2)</c> and returns
    /// <paramref name="key"/>'s value. Buffer layout: <c>[int argc][exec_path\0][\0…][argv[0..argc)\0][env…\0]</c>.
    /// Works for a same-uid process (the daemon's own children); null on any read/parse failure.</summary>
    static string? ReadAgentEnvMacOs(int pid, string key) {
        int[] mib  = [CtlKern, KernProcArgs2, pid];
        nuint size = 0;

        if (Sysctl(mib, (uint) mib.Length, null, ref size, IntPtr.Zero, 0) != 0 || size == 0) return null;

        var buf = new byte[size];
        if (Sysctl(mib, (uint) mib.Length, buf, ref size, IntPtr.Zero, 0) != 0) return null;

        var len = (int) size;
        if (len < 4) return null;

        var argc = BitConverter.ToInt32(buf, 0);
        var p    = 4;

        // exec_path, then its trailing NUL padding
        while (p < len && buf[p] != 0) p++;
        while (p < len && buf[p] == 0) p++;

        // skip argc arguments
        for (var i = 0; i < argc && p < len; i++) {
            while (p < len && buf[p] != 0) p++;
            p++;
        }

        var prefix = key + "=";
        while (p < len) {
            var start = p;
            while (p < len && buf[p] != 0) p++;

            if (p > start) {
                var entry = Encoding.UTF8.GetString(buf, start, p - start);
                if (entry.StartsWith(prefix, StringComparison.Ordinal))
                    return entry[prefix.Length..];
            }

            p++;
        }

        return null;
    }
}
