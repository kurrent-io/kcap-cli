using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>L1-managed(b): the REAL production entry point (UnixPtyProcessFactory.Spawn) end
/// to end — resolves PATH in the parent, builds a plan via pty_preflight, spawns via the
/// dedicated spawner thread, and surfaces the natively-captured StartIdentity.</summary>
public class UnixPtyProcessSpawnTests {
    [Test]
    public async Task Spawn_produces_a_running_process_with_a_captured_identity() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // The spawner thread is a non-background (IsBackground = false) OS thread — the test
        // must own it and Dispose() it itself, or the thread parks forever on the queue and the
        // whole test-host process never exits (confirmed empirically: an earlier version of this
        // test that let UnixPtyProcessFactory own an undisposed static singleton hung indefinitely).
        using var spawner = new UnixSpawnerThread();
        var       factory = new UnixPtyProcessFactory(spawner);
        var       proc    = factory.Spawn("sleep", ["5"], Directory.GetCurrentDirectory());
        try {
            await Assert.That(proc.Pid).IsGreaterThan(0);
            await Assert.That(proc.StartIdentity).IsNotNull();
            await Assert.That(proc.StartIdentity).IsNotEmpty();
            await Assert.That(proc.StartIdentity).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");

            // Cross-check against the independent, existing ProcessStartToken machinery: on
            // Linux both the shim's capture_lx_identity (pty_shim.c) and ProcessStartToken.ForPid
            // read the SAME two kernel facts (starttime field 22 of /proc/{pid}/stat, and
            // /proc/sys/kernel/random/boot_id) in the SAME "lx:{boot}:{starttime}" format via two
            // independently-implemented code paths, so they must produce byte-identical tokens
            // for a healthy spawn.
            //
            // On macOS, as of M1-A(a) [Task 7], ProcessStartToken.ForPid ALSO vendors the SAME
            // proc_pidinfo(PROC_PIDUNIQIDENTIFIERINFO=17) + kern.bootsessionuuid capture pty_shim.c's
            // pty_capture_mac_identity uses natively (same fixed-offset prefix read, same 256-byte
            // buffer size) — so the two independently-implemented code paths must ALSO produce
            // byte-identical "mac:{bootsessionuuid}:{p_uniqueid}" tokens for a healthy spawn. This
            // is the regression guard against the two vendored copies of the private struct
            // drifting apart (before Task 7 landed, Core's non-Linux branch returned the OLDER
            // "tk:{Process.StartTime.Ticks}" scheme instead, so this cross-check did not apply on
            // macOS and was gated out below).
            var liveToken = Capacitor.Cli.Core.ProcessStartToken.ForPid(proc.Pid);
            await Assert.That(liveToken).IsEqualTo(proc.StartIdentity);
        } finally {
            await proc.DisposeAsync();
        }
    }
}
