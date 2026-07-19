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
    public async Task Job_sets_no_breakaway_flag_so_escape_is_impossible() {
        if (!OperatingSystem.IsWindows()) return;

        // Structural proof that a hosted descendant cannot CreateProcess its way out of the job:
        // the production job carries EXACTLY JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and NEITHER
        // breakaway-allowed flag, so a CREATE_BREAKAWAY_FROM_JOB child fails with
        // ERROR_ACCESS_DENIED by construction. We PROVE this by querying the job's limit flags —
        // NOT by joining this test host to proc's killing job and disposing it (which would
        // close the last handle and have the OS kill the host). Reading the flags is host-safe;
        // proc.DisposeAsync closes proc's own killing job, killing proc's child, never the host.
        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "exit"], Directory.GetCurrentDirectory());

        var limitFlags = ConPtyJobObjectTestHelper.QueryJobLimitFlags(ConPtyInteropTestAccessor.JobHandle(proc));

        await Assert.That(limitFlags & ConPtyInterop.JOB_OBJECT_LIMIT_BREAKAWAY_OK).IsEqualTo(0u);
        await Assert.That(limitFlags & ConPtyInterop.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK).IsEqualTo(0u);
        await Assert.That(limitFlags).IsEqualTo(ConPtyInterop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE);
    }

    [Test]
    public async Task A_daemon_already_inside_an_outer_job_still_nests() {
        if (!OperatingSystem.IsWindows()) return;

        // Put THIS test process into an outer job with no UI-restriction flags (nesting is
        // only blocked when either job carries UI limits) — mirrors "the daemon happens to be
        // launched inside another job" (e.g. a CI runner, a service wrapper).
        //
        // HOST-SAFETY INVARIANT: outerJob is a PLAIN CreateJobObjectW with NO
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so joining the test host to it is safe — a plain
        // job does NOT kill its members when its last handle closes. Clean up with CloseHandle,
        // never TerminateJobObject (which would kill every member, including this test host).
        var outerJob = ConPtyInterop.CreateJobObjectW(IntPtr.Zero, null);
        ConPtyJobObjectTestHelper.AssignSelfToJob(outerJob);

        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "timeout /t 5 >NUL"], Directory.GetCurrentDirectory());

        // Nesting succeeded iff the spawn didn't throw AND the child is (transitively) a
        // member of the outer job too — checked via the native IsProcessInJob.
        await Assert.That(ConPtyJobObjectTestHelper.IsProcessInJob(proc.Pid, outerJob)).IsTrue();

        // CloseHandle (NOT TerminateJobObject) — see the invariant above. Windows has no API to
        // un-assign a process from a job, so the test host stays a member of this now-handleless
        // plain job for the rest of the run. That accumulation is harmless (a plain job imposes
        // nothing); at worst a later self-join could fail with a nesting error, which fails that
        // test cleanly and can no longer kill the host now that the killing/terminate paths are gone.
        ConPtyInterop.CloseHandle(outerJob);
    }

    [Test]
    public async Task Job_creation_failure_fails_the_spawn_closed() {
        if (!OperatingSystem.IsWindows()) return;

        // "Nesting genuinely prevented": a process inside a UI-restricted job cannot form a
        // nested job, so ConPtyProcess.Spawn must fail CLOSED (throw, no uncontained child)
        // rather than silently launch one.
        //
        // This assertion runs OUT-OF-PROCESS, in a disposable NativeTestHost, precisely because
        // assigning a process to a UI-restricted job is IRREVERSIBLE (Windows has no un-assign
        // API). Doing it in THIS shared test host — as an earlier in-process version did — would
        // permanently poison later job NESTING and make Disposing_the_process_kills_child_and_grandchild
        // and Job_sets_no_breakaway_flag... throw whenever they were scheduled after it. [NotInParallel]
        // + ordering cannot fix an irreversible mutation, so the poisoning lives in its own process
        // (the same pattern the Linux PDEATHSIG/FailFast tests use). The child assigns ITSELF to a
        // UI-restricted job, attempts a real Spawn, and exits: 0 = failed closed (Spawn threw),
        // 20 = leaked an uncontained child, 30 = could not set up the poison job.
        using var host   = StartHost("win-ui-restricted-fail-closed");
        var       exited = host.WaitForExit(30000);

        await Assert.That(exited).IsTrue();
        await Assert.That(host.ExitCode).IsEqualTo(0); // 0 == proven fail-closed
    }

    static bool IsProcessAlive(int pid) {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // Launches the sibling NativeTestHost project as a separate process running <paramref
    // name="mode"/>. Mirrors UnixSpawnerThreadTests' helper (kept local rather than shared to
    // avoid touching those tests); the fail-closed proof reads only the child's exit code.
    static Process StartHost(string mode) {
        var dll = ResolveNativeHostDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {mode}") {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start NativeTestHost");
    }

    static string ResolveNativeHostDll() {
        var dir      = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm      = Path.GetFileName(dir);
        var config   = Path.GetFileName(Path.GetDirectoryName(dir)!);
        var testRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var hostDll  = Path.Combine(testRoot, "Capacitor.Cli.Tests.Unit.NativeTestHost", "bin", config, tfm,
            "Capacitor.Cli.Tests.Unit.NativeTestHost.dll");

        if (!File.Exists(hostDll))
            throw new InvalidOperationException($"NativeTestHost not built at {hostDll} — build Capacitor.slnx first");

        return hostDll;
    }
}
