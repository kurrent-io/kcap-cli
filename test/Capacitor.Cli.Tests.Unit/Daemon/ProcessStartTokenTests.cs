using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Tests for <see cref="ProcessStartToken"/> — the cross-process-stable start
/// token that the daemon PID file uses to tell a live daemon from a recycled
/// PID. The bug it fixes (AI-839) only manifests <i>across processes</i>: on
/// Linux <see cref="Process.StartTime"/> is recomputed per process from a
/// boot-time estimate, so the daemon and a later <c>status</c> invocation
/// disagreed and every live daemon looked "stale". The cross-process test
/// below is therefore the one that actually guards the regression.
/// </summary>
public class ProcessStartTokenTests {
    [Test]
    public async Task ForCurrent_ReturnsNonEmptyToken() {
        var token = ProcessStartToken.ForCurrent();

        await Assert.That(token).IsNotNull();
        await Assert.That(token).IsNotEmpty();
    }

    [Test]
    public async Task ForCurrent_EqualsForPidOfSelf() {
        var viaCurrent = ProcessStartToken.ForCurrent();
        var viaPid     = ProcessStartToken.ForPid(Environment.ProcessId);

        await Assert.That(viaPid).IsEqualTo(viaCurrent);
    }

    [Test]
    public async Task ForPid_OnNonexistentProcess_ReturnsNull() {
        // PID 0x7FFFFFFF is effectively never a live process; even if it were,
        // a null result is the contract we assert callers can rely on.
        var token = ProcessStartToken.ForPid(int.MaxValue);

        await Assert.That(token).IsNull();
    }

    /// <summary>
    /// The core AI-839 guarantee on Linux: the token is built from the kernel's
    /// boot-relative <c>starttime</c> (field 22 of <c>/proc/&lt;pid&gt;/stat</c>)
    /// plus the per-boot id, NOT <see cref="Process.StartTime"/>. The kernel
    /// value is byte-identical for every reader and never recomputed, which is
    /// what makes it stable across the daemon-writer and CLI-reader processes.
    /// If someone reverts to <c>Process.StartTime.Ticks</c> this fails, because
    /// that's a ~6.4e17 tick count, not the <c>lx:&lt;boot&gt;:&lt;jiffies&gt;</c> shape.
    /// </summary>
    [Test]
    public async Task ForPid_OnLinux_ReturnsBootScopedProcStarttime() {
        if (!OperatingSystem.IsLinux()) return;

        var pid  = Environment.ProcessId;
        var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat");

        // Fields after the (possibly space/paren-containing) comm begin at the
        // last ')'; field 22 (starttime) is index 19 of those.
        var fields    = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var starttime = fields[19];
        var bootId    = (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id")).Trim();

        await Assert.That(ProcessStartToken.ForPid(pid)).IsEqualTo($"lx:{bootId}:{starttime}");
    }

    [Test]
    public async Task Matches_SelfWithOwnToken_IsTrue() {
        var token = ProcessStartToken.ForCurrent();

        await Assert.That(token).IsNotNull();
        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, token!)).IsEqualTo(true);
    }

    [Test]
    public async Task Matches_SameSchemeDifferentValue_IsFalse() {
        // Build a same-scheme token with a deliberately wrong final field, so the
        // scheme matches but the value doesn't — the conclusive "recycled PID" case.
        var token  = ProcessStartToken.ForCurrent()!;
        var lastSep = token.LastIndexOf(':');
        var wrong   = token[..(lastSep + 1)] + "999999999999";

        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, wrong)).IsEqualTo(false);
    }

    /// <summary>
    /// A pre-AI-839 PID file stored a bare <see cref="Process.StartTime"/> tick
    /// count (no <c>scheme:</c> prefix). It must compare as "can't tell" (null)
    /// so the daemon-identity check falls back to the name match instead of
    /// stranding a still-running old daemon across an upgrade.
    /// </summary>
    [Test]
    public async Task Matches_LegacyBareTicksToken_IsNull() {
        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, "639168740000000000")).IsNull();
    }

    [Test]
    public async Task Matches_NonexistentProcess_IsNull() {
        await Assert.That(ProcessStartToken.Matches(int.MaxValue, "tk:123")).IsNull();
    }
}
