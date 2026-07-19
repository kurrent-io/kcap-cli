using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

/// <summary>
/// A machine-local token identifying a specific process <i>incarnation</i>,
/// stable across separate reader processes. Stored on the second line of the
/// daemon PID file and compared to tell a live daemon apart from a recycled
/// PID.
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
/// match an unrelated process that happens to reuse the PID and tick offset.</para>
///
/// <para>On macOS (M1-A(a)) the token is
/// <c>mac:&lt;kern.bootsessionuuid&gt;:&lt;p_uniqueid&gt;</c>: <c>p_uniqueid</c> is
/// a kernel-assigned, monotonically-increasing per-process counter (never reused
/// within a boot session) read via the private <c>proc_pidinfo</c> flavor
/// <c>PROC_PIDUNIQIDENTIFIERINFO</c> (17), and <c>kern.bootsessionuuid</c> scopes it
/// to the current boot session so a value can't collide across reboots. This
/// duplicates (rather than reuses) the SAME vendored capture
/// <c>pty_shim.c</c>'s <c>pty_capture_mac_identity</c> uses natively, because
/// <c>Capacitor.Cli</c> (the CLI binary) calls <see cref="ForPid"/> without the
/// daemon's native shim present — see the M1-A design note. A cross-implementation
/// consistency test guards the two vendored copies from drifting apart.</para>
///
/// <para>On Windows <see cref="Process.StartTime"/> is an absolute timestamp
/// already identical across processes (and across reboots), so the token stays
/// <c>tk:&lt;ticks&gt;</c>.</para>
///
/// <para>The <c>scheme:</c> prefix lets <see cref="Matches"/> distinguish "a
/// different incarnation" (same scheme, different value — conclusive) from "a
/// token I can't compare" (a legacy PID file that stored bare
/// <see cref="Process.StartTime"/> ticks with no prefix, encountered mid-upgrade
/// while the old daemon is still running). The latter returns null so callers
/// fall back to a weaker image-name check instead of stranding a live daemon.</para>
/// </summary>
public static partial class ProcessStartToken {
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

        if (OperatingSystem.IsMacOS()) {
            // A live process must exist for this pid before trying the private-ABI capture —
            // proc_pidinfo on a gone pid returns a short/garbage read anyway, but checking via
            // Process first means a nonexistent pid returns null through the SAME path as every
            // other platform (a consistent contract for callers), and only THEN is the mac:
            // capture attempted. Falling through to tk: if that capture specifically fails would
            // be WRONG (it would silently downgrade a live, CONFIRMED-alive process to the weaker
            // wall-clock scheme) — so a capture failure here returns null (uncapturable),
            // matching the Linux contract's "value can't be read" case, never a scheme fallback.
            try {
                using var proc = Process.GetProcessById(pid);

                return CaptureMacToken(pid);
            } catch {
                return null;
            }
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

    // ── macOS: PROC_PIDUNIQIDENTIFIERINFO (flavor 17) is #ifdef PRIVATE in the public SDK's
    // sys/proc_info.h — proc_pidinfo() itself IS public, the flavor/struct are not declared.
    //
    // The struct's TOTAL size is not a stable, documented ABI: empirically probing THIS kernel
    // (varying the requested buffer size, then comparing getpid() vs a freshly forked child vs
    // pid 1) shows that any requested size below 56 bytes is refused outright (returns 0, never a
    // truncated fill) while 56+ always returns exactly 56 — and neither 56 nor any other commonly
    // cited historical total matches across OS versions. What DOES hold up across that same probe
    // is the field layout at the FRONT of the struct: the first 16 bytes are a per-process UUID,
    // the next 8 bytes (offset 16) are a monotonic unique-id counter (a freshly forked child's
    // value is exactly the parent's + 1), and the following 8 bytes (offset 24) in the child equal
    // the PARENT's offset-16 value (consistent with "parent's unique id") — including the pid-1
    // sentinel case (uniqueid=1, puniqueid=0). This is the historically-cited prefix layout
    // (p_uuid[16], p_uniqueid, p_puniqueid) for the three fields that matter.
    //
    // Rather than assert an exact total size that has ALREADY been observed to vary, this reads a
    // buffer generously larger than any known/plausible total (256 bytes) and only requires the
    // fixed-offset PREFIX to be present (a short/negative/zero-length read is uncapturable), so a
    // future OS growing the reserved tail keeps working with no code change. This is the SAME
    // buffer size and prefix-only read pty_shim.c's native pty_capture_mac_identity settled on
    // (see that file's design note above PTY_UNIQIDENTIFIERINFO_BUFSIZE) — the two vendored copies
    // must read the identical bytes at the identical offsets for a live pid, or the daemon's
    // native capture (at spawn) and this re-deriver (at reap-comparison time) would disagree about
    // the SAME process's identity. A cross-implementation consistency test
    // (ProcessStartTokenTests.ForPid_OnMacOS_MatchesNativeShimCaptureForSelf,
    // UnixPtyProcessSpawnTests.Spawn_produces_a_running_process_with_a_captured_identity) is the
    // regression guard against the two copies drifting apart.
    //
    // Verified on this dev box (macOS, arm64) only — the exact prefix offsets should ideally be
    // cross-checked against whatever OS version the release macOS runner actually uses (per
    // pty_shim.c's own flagged caveat: not gated in CI at all today — ci.yml has no macOS runner,
    // and release.yml only build-time-smokes the shim, it doesn't run these tests). The capture
    // already fails closed (null, never a wrong value) if this assumption is ever violated, so
    // this is a functionality risk (a spare/uncapturable result), never a false-proof risk.
    const int ProcPidUniqIdentifierInfo   = 17;
    const int UniqIdentifierInfoBufSize   = 256; // matches pty_shim.c's PTY_UNIQIDENTIFIERINFO_BUFSIZE
    const int UniqIdentifierInfoPrefixLen = 32;  // p_uuid[16] + p_uniqueid(8) + p_puniqueid(8) — the only verified-stable prefix

    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int sysctlbyname(string name, byte* oldp, nuint* oldlenp, IntPtr newp, nuint newlen);

    /// <summary>
    /// Vendored macOS capture: <c>mac:&lt;kern.bootsessionuuid&gt;:&lt;p_uniqueid&gt;</c>, or null
    /// if any step is uncapturable. Fail-safe is SPARE-shaped throughout: any short/negative read
    /// (kernel refusal, EINVAL, gone pid), a zero unique id, or a failed <c>sysctlbyname</c> call
    /// returns null — NEVER a wrong/garbage token.
    /// </summary>
    static unsafe string? CaptureMacToken(int pid) {
        var buf = new byte[UniqIdentifierInfoBufSize];
        int written;

        fixed (byte* p = buf) {
            written = proc_pidinfo(pid, ProcPidUniqIdentifierInfo, 0, p, buf.Length);
        }

        // Fail-safe: only a read that covers at least the verified prefix proves anything. A
        // short read (kernel's min-size refusal), a negative return (error), or anything else
        // that doesn't reach the prefix is uncapturable — never a false proof, never a
        // wrong-offset token from a struct shape we haven't verified.
        if (written < UniqIdentifierInfoPrefixLen) return null;

        var uniqueId = BitConverter.ToUInt64(buf, 16); // p_uuid[16] then p_uniqueid @ offset 16

        if (uniqueId == 0) return null;

        Span<byte> uuidBuf = stackalloc byte[64];
        nuint      uuidLen = (nuint) uuidBuf.Length;

        fixed (byte* up = uuidBuf) {
            if (sysctlbyname("kern.bootsessionuuid", up, &uuidLen, IntPtr.Zero, 0) != 0) return null;
        }

        if (uuidLen == 0) return null;

        var uuidStr = System.Text.Encoding.UTF8.GetString(uuidBuf[..(int) (uuidLen - 1)]); // drop the trailing NUL

        return $"mac:{uuidStr}:{uniqueId}";
    }
}
