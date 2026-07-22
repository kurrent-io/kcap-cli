using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LedgerHookTests {
    static (AgentPidRecordStore store, ResolvedCandidatesLedger ledger, string dir) NewPair() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-hook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new AgentPidRecordStore(dir, NullLogger.Instance), new ResolvedCandidatesLedger(dir, NullLogger.Instance), dir);
    }

    [Test]
    public async Task Record_pass_appends_resolved_evidence_before_deleting_the_source() {
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // process gone -> confirmed absent

        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        var reaper = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await reaper.ReapOnceAsync();

        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(entry.FlowRole).IsEqualTo("reviewer"); // from the trusted record
        await Assert.That(store.ReadAll()).IsEmpty();            // source deleted after the append
    }

    [Test]
    public async Task Crash_between_append_and_delete_reconciles_idempotently_on_restart() {
        var (store, ledger, dir) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Simulate crash-after-append-before-delete: append, but skip the delete this pass.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => { ledger.Upsert(a, e, fr, role); throw new IOException("crash before delete"); });
        try { await crashing.ReapOnceAsync(); } catch { /* the reaper swallows per-record faults */ }
        await Assert.That(store.ReadAll()).IsNotEmpty();          // leftover source
        var gen = ledger.Snapshot().Single().Generation;

        // Restart: re-derive from the leftover source; Upsert collapses onto the committed entry.
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        await Assert.That(store.ReadAll()).IsEmpty();             // leftover source now deleted
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(gen); // single emit, no new generation
    }

    [Test]
    public async Task Crash_before_append_re_derives_the_record_pass_evidence_next_boot() {
        // Parent §8: crash BEFORE the durable append leaves the source only → re-derived next boot
        // (at-least-once, deduped). The record pass confirms death but the daemon dies before Upsert.
        var (store, ledger, _) = NewPair();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; var id = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        store.Write(new AgentPidRecord("gone", pid, id, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", "did", "old-epoch", DateTimeOffset.UtcNow));

        // Crash before the append: the confirmed-gone branch throws before touching the ledger.
        var crashing = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (_, _, _, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-record faults swallowed */ }
        await Assert.That(ledger.Snapshot()).IsEmpty();          // nothing committed
        await Assert.That(store.ReadAll()).IsNotEmpty();         // source persists

        // Next boot re-derives from the on-disk pre-append source shape (keyed (AgentId, OldEpoch)).
        var restarted = new OrphanReaper(store, "did", "new-epoch", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => ledger.Upsert(a, e, fr, role));
        await restarted.ReapOnceAsync();
        var entry = ledger.Snapshot().Single();
        await Assert.That(entry.AgentId).IsEqualTo("gone");
        await Assert.That(entry.OldEpoch).IsEqualTo("old-epoch");
        await Assert.That(store.ReadAll()).IsEmpty();
    }
}
