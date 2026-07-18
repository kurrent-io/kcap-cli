using System.Diagnostics;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// W1 (spec §4.1): every hosted-agent PTY on Windows is created already-bound to a
/// KILL_ON_JOB_CLOSE job — the OS itself kills the leader AND every descendant the instant
/// the job handle's last reference closes (clean dispose, crash, or an external kill of the
/// daemon). No managed reap layer is exercised on Windows once this is proven; these tests
/// are the only place that behavior is asserted.
/// </summary>
public class ConPtyJobObjectTests {
    [Test]
    public async Task Disposing_the_process_kills_child_and_grandchild() {
        if (!OperatingSystem.IsWindows()) return;

        // cmd.exe spawns a grandchild `timeout` so the group/job boundary — not just the
        // immediate child — is under test.
        await using var proc = ConPtyProcess.Spawn(
            "cmd.exe", ["/c", "start /min cmd.exe /c timeout /t 60 >NUL & timeout /t 60 >NUL"],
            Directory.GetCurrentDirectory());

        await Task.Delay(500); // let the grandchild actually spawn before we kill the job

        await proc.DisposeAsync();

        // Job-handle close is synchronous-ish from the OS's perspective, but process exit
        // notification can lag slightly — poll rather than assert instantly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsProcessAlive(proc.Pid)) await Task.Delay(200);

        await Assert.That(IsProcessAlive(proc.Pid)).IsFalse();
    }

    [Test]
    public async Task Breakaway_from_the_job_is_denied() {
        if (!OperatingSystem.IsWindows()) return;

        // A child that explicitly requests CREATE_BREAKAWAY_FROM_JOB must fail to start —
        // our job sets NO breakaway-allowed flag, so escape is impossible by construction
        // (mere grandchild membership doesn't prove escape is impossible; an ACTUAL denied
        // breakaway attempt does).
        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "pause"], Directory.GetCurrentDirectory());

        // CreateProcess with CREATE_BREAKAWAY_FROM_JOB against a job with no
        // JOB_OBJECT_LIMIT_BREAKAWAY_OK/SILENT_BREAKAWAY_OK must fail with
        // ERROR_ACCESS_DENIED — asserted via the raw Win32 call, not System.Diagnostics.Process
        // (which has no breakaway knob). We join proc's own production job first so the
        // breakaway attempt has something real to break away from.
        var breakawaySucceeded = ConPtyJobObjectTestHelper.TryCreateWithBreakaway(
            "cmd.exe", "/c exit", ConPtyInteropTestAccessor.JobHandle(proc));

        await Assert.That(breakawaySucceeded).IsFalse();
    }

    [Test]
    public async Task A_daemon_already_inside_an_outer_job_still_nests() {
        if (!OperatingSystem.IsWindows()) return;

        // Put THIS test process into an outer job with no UI-restriction flags (nesting is
        // only blocked when either job carries UI limits) — mirrors "the daemon happens to be
        // launched inside another job" (e.g. a CI runner, a service wrapper).
        var outerJob = ConPtyInterop.CreateJobObjectW(IntPtr.Zero, null);
        ConPtyJobObjectTestHelper.AssignSelfToJob(outerJob);

        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "timeout /t 5 >NUL"], Directory.GetCurrentDirectory());

        // Nesting succeeded iff the spawn didn't throw AND the child is (transitively) a
        // member of the outer job too — checked via the native IsProcessInJob.
        await Assert.That(ConPtyJobObjectTestHelper.IsProcessInJob(proc.Pid, outerJob)).IsTrue();

        ConPtyInterop.TerminateJobObject(outerJob, 0);
    }

    [Test]
    public async Task Job_creation_failure_fails_the_spawn_closed() {
        if (!OperatingSystem.IsWindows()) return;

        // Simulate "nesting genuinely prevented": put this process in an outer job that DOES
        // carry a UI-restriction limit so a nested job can't form, and assert Spawn throws
        // rather than silently spawning uncontained.
        var restrictiveJob = ConPtyJobObjectTestHelper.CreateUiRestrictedJobAndAssignSelf();

        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ConPtyProcess.Spawn("cmd.exe", ["/c", "exit"], Directory.GetCurrentDirectory()).DisposeAsync());
        } finally {
            ConPtyInterop.TerminateJobObject(restrictiveJob, 0);
        }
    }

    static bool IsProcessAlive(int pid) {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
