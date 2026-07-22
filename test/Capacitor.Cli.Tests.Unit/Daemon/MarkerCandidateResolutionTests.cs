using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class MarkerCandidateResolutionTests {
    static (AgentPidRecordStore store, MarkerCandidateStore markers, string dir) New() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-mk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (new AgentPidRecordStore(dir, NullLogger.Instance), new MarkerCandidateStore(dir, NullLogger.Instance), dir);
    }

    [Test]
    public async Task Recordless_marker_kill_emits_resolved_and_deletes_the_source() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"] = "rec-less", ["KCAP_DAEMON_ID"] = "did", ["KCAP_DAEMON_EPOCH"] = "old" });

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await Assert.That(dummy.HasExited).IsTrue();
        await Assert.That(resolved).Contains(("rec-less", "old"));
        await Assert.That(markers.ReadAll()).IsEmpty();          // source deleted after the emit
    }

    [Test]
    public async Task Alive_mismatch_spares_and_leaves_the_marker_candidate_pending() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        // A persisted marker-candidate whose pid is now occupied by an UNRELATED process (no triple).
        using var occupant = DummyProcess.StartSleep(30); // no KCAP_* env
        markers.Write(new MarkerCandidate("stale", "did", "old", occupant.Pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync(); // boot reconciliation re-reads the source, re-runs (a)/(b)/(c)

        await Assert.That(occupant.HasExited).IsFalse();          // reused pid spared
        await Assert.That(resolved).IsEmpty();                    // (c) never emits
        await Assert.That(markers.ReadAll().Single().AgentId).IsEqualTo("stale"); // stays pending
        occupant.Kill();
    }

    [Test]
    public async Task Confirmed_dead_persisted_candidate_resolves_incl_zombie_path() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive
        markers.Write(new MarkerCandidate("dead1", "did", "old", pid));

        var resolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        await Assert.That(resolved).Contains(("dead1", "old")); // (a) dead -> resolved+emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    // ── Marker-scan kill hook — crash both sides (parent §8; recordless→marker-candidate case). ──
    [Test]
    public async Task Marker_kill_crash_before_append_re_derives_from_the_persisted_source() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));

        // Crash BEFORE the emit: onMarkerResolved throws before the ledger append -> no entry, source persists.
        var crashing = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (_, _) => throw new IOException("crash before append"));
        try { await crashing.ReapOnceAsync(); } catch { /* per-source faults swallowed */ }
        await Assert.That(markers.ReadAll()).IsNotEmpty(); // source persists (never a source-less window)

        // Next boot reconciles: re-read the on-disk source, (a) dead -> single emit + delete.
        var resolved = new List<(string, string)>();
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => resolved.Add((a, e)));
        await restarted.ReapOnceAsync();
        await Assert.That(resolved).IsEquivalentTo(new[] { ("mk-gone", "old") });
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Marker_kill_crash_between_append_and_source_delete_reconciles_idempotently() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, dir) = New();
        var ledger = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        markers.Write(new MarkerCandidate("mk-gone", "did", "old", pid));
        var committed = ledger.Upsert("mk-gone", "old", null, null); // append happened
        // crash before markerStore.Delete -> committed entry + leftover marker source

        // Next boot: reconciliation re-reads the source, (a) dead -> idempotent Upsert (key (AgentId,OldEpoch)) + delete.
        var restarted = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            markerStore: markers, onMarkerResolved: (a, e) => ledger.Upsert(a, e, null, null));
        await restarted.ReapOnceAsync();
        await Assert.That(ledger.Snapshot().Single().Generation).IsEqualTo(committed.Generation); // single emit
        await Assert.That(markers.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task Identity_unavailable_record_resolved_via_marker_scan_emits_trusted_flow() {
        if (!OperatingSystem.IsLinux()) return;
        var (store, markers, _) = New();
        using var dummy = DummyProcess.StartSleep(30);
        var pid = dummy.Pid; dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // dead per IsAlive (branch a)
        // A Linux identity_unavailable RECORD (capture failed) co-existing with a marker-candidate source.
        store.Write(new AgentPidRecord("iu", pid, "", PidIdentityKind.IdentityUnavailable, "ReviewFlow",
            "codex", "flow-9", "reviewer", "did", "old", DateTimeOffset.UtcNow));
        markers.Write(new MarkerCandidate("iu", "did", "old", pid));

        var recordResolved = new List<(string a, string e, string? fr, string? role)>();
        var markerResolved = new List<(string, string)>();
        var reaper = new OrphanReaper(store, "did", "new", NullLogger.Instance,
            onRecordResolved: (a, e, fr, role) => recordResolved.Add((a, e, fr, role)),
            markerStore: markers, onMarkerResolved: (a, e) => markerResolved.Add((a, e)));
        await reaper.ReapOnceAsync();

        // The trusted record's flow is emitted (via onRecordResolved), NOT null (via onMarkerResolved).
        await Assert.That(recordResolved.Single().role).IsEqualTo("reviewer");
        await Assert.That(recordResolved.Single().fr).IsEqualTo("flow-9");
        await Assert.That(markerResolved).IsEmpty();
        await Assert.That(store.ReadAll()).IsEmpty();       // identity_unavailable record cleared
        await Assert.That(markers.ReadAll()).IsEmpty();     // marker source cleared
    }
}
