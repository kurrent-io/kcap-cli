using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// Every pty_spawn call runs on one dedicated, daemon-lifetime thread, never a pool thread:
/// PR_SET_PDEATHSIG is tied to the creating thread, so a pool thread retiring would SIGKILL every
/// agent it spawned. The tests that kill or crash the spawning process watch it from outside,
/// through <see cref="NativeTestHostProcess"/>.
/// </summary>
public class UnixSpawnerThreadTests {
    [Test]
    public async Task Pdeathsig_kills_the_child_when_the_spawner_process_dies() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = NativeTestHostProcess.Start("spawn-dummy");
        var childPid = await ReadPidLineAsync(host);
        // The child belongs to the host process, not to us, so once pdeathsig fires and init reaps
        // it the pid is free — assert on the incarnation, not the number.
        var childIdentity = PidIdentity.Capture(childPid);

        host.Kill(entireProcessTree: false); // simulate an external daemon crash (SIGKILL)
        host.WaitForExit(5000);

        await PidIdentity.WaitUntilGoneAsync(childPid, childIdentity, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Unexpected_spawner_thread_exit_fail_fasts_the_host_process() {
        if (!OperatingSystem.IsLinux()) return;

        using var host = NativeTestHostProcess.Start("crash-spawner");
        var exited = host.WaitForExit(10000);

        await Assert.That(exited).IsTrue();
        // Environment.FailFast raises SIGABRT on Unix — .NET reports that as a large/negative
        // native exit code (128 + signal, i.e. 134), NOT a clean 0. Assert it's non-zero rather
        // than pin the exact platform-dependent encoding.
        await Assert.That(host.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task Agent_survives_unrelated_pool_thread_churn_while_the_thread_lives() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();

        // Churn several short-lived pool threads BEFORE and AFTER the spawn — none of them is
        // the thread that called pty_spawn, so none of their deaths should matter.
        for (var i = 0; i < 5; i++) await Task.Run(() => { });

        // envp must be a NULL-terminated array (mirrors execve/PtySpawnTests' EmptyEnvp()) —
        // pty_preflight/pty_spawn walk it as `char *const envp[]` until a NULL sentinel, so a
        // genuinely zero-length managed array (no trailing null) would read past the end of
        // whatever the marshaller allocated. Probe execveat support rather than hardcoding 1
        // (mirrors PtySpawnTests.Preflight): macOS always reports 0, and forcing an EXEC_FD plan
        // there fails at the exec step (pty_spawn has no fd-exec primitive off Linux).
        var execveatSupported = UnixPtyInterop.pty_probe_execveat();
        var rc = UnixPtyInterop.pty_preflight("/bin/sleep", ["sleep", "3", null], [null], execveatSupported, out var plan);
        await Assert.That(rc).IsEqualTo(0);

        var result = spawner.SpawnOn(plan, [null], AppContext.BaseDirectory, 40, 120, Environment.ProcessId, -1);
        try {
            await Assert.That(result.Pid).IsGreaterThan(0);
            for (var i = 0; i < 5; i++) await Task.Run(() => { });
            await Task.Delay(300);
            // Still the SAME incarnation. kill(pid, 0) would also pass on a squatter that took the
            // number over after the agent died — the exact failure this test exists to catch.
            await Assert.That(PidIdentity.IsGone(result.Pid, result.StartIdentityString)).IsFalse();
        } finally {
            // Guard against pid<=0: kill(0, sig)/kill(-1, sig) have special "whole process
            // group"/"every process" meanings on Unix — never pass through a failed spawn's
            // Pid=0 (a real bug caught while writing this test: it SIGKILLed this entire test
            // host's process group instead of a single child).
            if (result.Pid > 0) {
                UnixPtyInterop.kill(result.Pid, UnixPtyInterop.SIGKILL);
                UnixPtyInterop.waitpid(result.Pid, out _, 0);
            }
            var p = plan; UnixPtyInterop.pty_plan_free(ref p);
        }
    }

    [Test]
    public async Task Dispose_joins_the_spawner_thread_before_returning() {
        // Regression for the dispose race (Q2): Dispose must not return — and must not dispose
        // the BlockingCollection — until the loop thread has actually exited, otherwise it could
        // free the queue out from under a still-running GetConsumingEnumerable(). With no spawn
        // in flight the CompleteAdding()+Join() completes near-instantly; the assertion is that
        // the thread is provably gone once Dispose returns. Pure managed lifecycle (the loop only
        // parks on the empty queue — no pty_spawn, no shim), so it runs on any platform.
        var spawner = new UnixSpawnerThread();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        spawner.Dispose();

        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    static async Task<int> ReadPidLineAsync(Process host) {
        var line = await host.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("no PID line from host");
        return int.Parse(line["PID=".Length..], CultureInfo.InvariantCulture);
    }
}
