using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Phase B (D4 §6.4(3)): <see cref="OrphanReaper"/> — reaps hosted-agent children that outlived
/// a prior daemon run. Acts only on test-owned <see cref="DummyProcess"/> instances. OS-aware: the
/// env-dependent reaps confirm-and-kill on Linux (readable <c>/proc/{pid}/environ</c>) but SPARE on
/// macOS 26 (env redacted — ambiguity never kills); the epoch-guard and gone-process paths are
/// OS-independent.
/// </summary>
public class OrphanReaperTests {
    static AgentPidRecordStore NewStore() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-orphan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return new AgentPidRecordStore(dir, NullLogger.Instance);
    }

    static AgentPidRecord Rec(string agentId, int pid, string identity, string daemonId, string epoch) =>
        new(agentId, pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex", "flow-1", "reviewer", daemonId, epoch, DateTimeOffset.UtcNow);

    [Test]
    public async Task Record_pass_reaps_a_prior_incarnation_survivor_and_deletes_the_record() {
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "orphan" });
        var identity = ProcessIdentity.Capture(dummy.Pid)!;

        store.Write(Rec("orphan", dummy.Pid, identity, daemonId: "did", epoch: "old-epoch"));

        var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        // Record-pass kills once ownership is provable: Linux confirms via the KCAP_AGENT_ID env,
        // Windows has no Unix env guard so an exact (pid, start-identity) match suffices. Only macOS 26
        // spares (env redacted → the Unix env guard can't confirm; ambiguity never kills).
        if (!OperatingSystem.IsMacOS()) {
            dummy.WaitForExit(TimeSpan.FromSeconds(8));
            await Assert.That(dummy.HasExited).IsTrue();
            await Assert.That(store.ReadAll()).IsEmpty();
        } else {
            // macOS: env unreadable → SPARED; process alive, record retained for a later attempt.
            await Assert.That(dummy.HasExited).IsFalse();
            await Assert.That(store.ReadAll().Any(r => r.AgentId == "orphan")).IsTrue();
            dummy.Kill();
        }
    }

    [Test]
    public async Task Record_pass_leaves_a_current_incarnation_record_and_its_live_agent_untouched() {
        // The epoch guard is the load-bearing safety property for the HEARTBEAT re-run: a record whose
        // epoch equals the running daemon's must NEVER be reaped — it belongs to a live current agent.
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "live" });
        var identity = ProcessIdentity.Capture(dummy.Pid)!;

        store.Write(Rec("live", dummy.Pid, identity, daemonId: "did", epoch: "cur-epoch"));

        // Same currentEpoch as the record → must be skipped entirely (before any env read).
        var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "cur-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        await Assert.That(dummy.HasExited).IsFalse();
        await Assert.That(store.ReadAll().Any(r => r.AgentId == "live")).IsTrue();
        dummy.Kill();
    }

    [Test]
    public async Task Record_pass_deletes_a_stale_record_whose_process_is_already_gone() {
        var store = NewStore();
        using var dummy = DummyProcess.StartSleep(30);
        var identity = ProcessIdentity.Capture(dummy.Pid)!;
        var pid = dummy.Pid;
        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(5));

        store.Write(Rec("gone", pid, identity, daemonId: "did", epoch: "old-epoch"));

        var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        // Process gone (or PID reused → proven identity mismatch) → the stale record is deleted.
        await Assert.That(store.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Env_marker_scan_reaps_a_stale_epoch_survivor_of_this_daemon_only() {
        using var stale = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "s", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old" });
        using var other = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "o", ["KCAP_DAEMON_ID"] = "OTHER", ["KCAP_DAEMON_EPOCH"] = "old" });
        using var mine = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "m", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "new" });

        // Empty store → the record pass is a no-op; only the env-marker scan runs.
        var reaper = new OrphanReaper(NewStore(), daemonId: "did", currentEpoch: "new", NullLogger.Instance);
        await reaper.ReapOnceAsync();

        if (OperatingSystem.IsLinux()) {
            stale.WaitForExit(TimeSpan.FromSeconds(8));
            await Assert.That(stale.HasExited).IsTrue();  // this daemon, prior epoch → reaped
            await Assert.That(other.HasExited).IsFalse(); // different daemon → spared
            await Assert.That(mine.HasExited).IsFalse();  // current epoch → spared
        } else {
            // macOS: env of another process can't be read → the scan finds nothing → all spared.
            await Assert.That(stale.HasExited).IsFalse();
            await Assert.That(other.HasExited).IsFalse();
            await Assert.That(mine.HasExited).IsFalse();
        }

        other.Kill();
        mine.Kill();
        stale.Kill();
    }
}
