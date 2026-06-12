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
    /// The core AI-839 guarantee on Linux: the token is the kernel's
    /// boot-relative <c>starttime</c> (field 22 of <c>/proc/&lt;pid&gt;/stat</c>),
    /// NOT <see cref="Process.StartTime"/>. The kernel value is byte-identical
    /// for every reader and never recomputed, which is exactly what makes it
    /// stable across the daemon-writer and CLI-reader processes. If someone
    /// reverts to <c>Process.StartTime.Ticks</c> this fails, because that's a
    /// ~6.4e17 tick count, not the ~4.6e7 jiffies field.
    /// </summary>
    [Test]
    public async Task ForPid_OnLinux_ReturnsProcStarttimeField() {
        if (!OperatingSystem.IsLinux()) return;

        var pid  = Environment.ProcessId;
        var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat");

        // Fields after the (possibly space/paren-containing) comm begin at the
        // last ')'; field 22 (starttime) is index 19 of those.
        var fields    = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var starttime = fields[19];

        await Assert.That(ProcessStartToken.ForPid(pid)).IsEqualTo(starttime);
    }
}
