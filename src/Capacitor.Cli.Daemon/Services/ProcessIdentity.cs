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
    /// gone or the value can't be read.</summary>
    public static string? Capture(int pid) => ProcessStartToken.ForPid(pid);

    /// <summary>True IFF a live process at <paramref name="pid"/> has EXACTLY
    /// <paramref name="expectedIdentity"/> (AI-839 tri-state == true). A gone pid, a recycled pid
    /// (different incarnation), or an uncomparable token all return false — ambiguity never kills.</summary>
    public static bool Matches(int pid, string expectedIdentity) =>
        ProcessStartToken.Matches(pid, expectedIdentity) == true;

    /// <summary>Best-effort liveness of <paramref name="pid"/> regardless of identity — only used to
    /// decide whether it's worth probing identity/env. Unix: <c>kill(pid, 0) == 0</c> (ESRCH → dead);
    /// Windows: <see cref="Process.GetProcessById(int)"/> succeeds.</summary>
    public static bool IsAlive(int pid) {
        if (OperatingSystem.IsWindows()) {
            try { using var _ = Process.GetProcessById(pid); return true; }
            catch { return false; }
        }

        return UnixPtyInterop.kill(pid, 0) == 0;
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
