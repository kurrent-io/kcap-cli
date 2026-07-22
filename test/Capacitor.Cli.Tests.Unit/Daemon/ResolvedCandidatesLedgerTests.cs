using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class ResolvedCandidatesLedgerTests {
    static ResolvedCandidatesLedger New(out string dir) {
        dir = Path.Combine(Path.GetTempPath(), "kcap-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return new ResolvedCandidatesLedger(dir, NullLogger.Instance);
    }

    [Test] public async Task Upsert_is_idempotent_on_agent_and_epoch_and_monotonic_generation() {
        var l = New(out _);
        var a = l.Upsert("a1", "e0", null, null);
        var b = l.Upsert("a2", "e0", null, null);
        var aAgain = l.Upsert("a1", "e0", null, null); // same key -> same generation, no new entry
        await Assert.That(a.Generation).IsEqualTo(1L);
        await Assert.That(b.Generation).IsEqualTo(2L);
        await Assert.That(aAgain.Generation).IsEqualTo(1L);
        await Assert.That(l.Snapshot().Count).IsEqualTo(2);
    }

    [Test] public async Task Snapshot_and_generation_survive_restart() {
        var l = New(out var dir);
        l.Upsert("a1", "e0", "flow", "reviewer");
        var reopened = new ResolvedCandidatesLedger(dir, NullLogger.Instance);
        await Assert.That(reopened.Snapshot().Single().AgentId).IsEqualTo("a1");
        // A new key after restart must not reuse generation 1.
        await Assert.That(reopened.Upsert("a2", "e0", null, null).Generation).IsEqualTo(2L);
    }

    [Test] public async Task Ack_prunes_sparsely_without_head_of_line_blocking() {
        var l = New(out _);
        var g1 = l.Upsert("a1", "e0", null, null);
        var g2 = l.Upsert("a2", "e0", null, null);
        var g3 = l.Upsert("a3", "e0", null, null);
        l.Ack([new ResolvedCandidateAck(g1.Generation, "a1", "e0"),
               new ResolvedCandidateAck(g3.Generation, "a3", "e0")]);
        await Assert.That(l.Snapshot().Select(x => x.AgentId)).IsEquivalentTo(new[] { "a2" });
        l.Ack([new ResolvedCandidateAck(g2.Generation, "a2", "e0")]); // idempotent for already-pruned too
        l.Ack([new ResolvedCandidateAck(99, "nope", "e0")]);          // unknown -> no-op
        await Assert.That(l.Snapshot()).IsEmpty();
    }
}
