using System.Text;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Handles stay within 16 bytes; a verdict's tokens expand only to refs the ledger minted or delivered, in order,
/// deduplicated, the first 200 kept and every other token counted as dropped.</summary>
public class JudgeCiteHandlesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    JudgeLedger LedgerWith(IReadOnlyDictionary<string, string> cites, (string, long, long)[] revisions, (string, int)[] turns) {
        var path = Tmp.PathTo($"{Guid.NewGuid():N}.jsonl");
        using (var w = JudgeLedgerWriter.Create(path, new JudgeLedgerHeader("run", "q", "v", new(48, 600_000, 65_536), null, DateTimeOffset.UnixEpoch)))
            w.Append(new JudgeLedgerPage(1, "p1", "read_events", "{}", "AgentSession-r", "{}", revisions, turns, [], [], cites, false, null));
        return JudgeLedgerReader.Read(path);
    }

    [Test]
    public async Task Handles_follow_the_grammar_and_fit_sixteen_bytes() {
        await Assert.That(JudgeCiteHandles.Page(1)).IsEqualTo("p1");
        await Assert.That(JudgeCiteHandles.Seeded(0)).IsEqualTo("o0");
        await Assert.That(JudgeCiteHandles.Row("p3", 2)).IsEqualTo("p3.2");
        await Assert.That(JudgeCiteHandles.OneShot(0)).IsEqualTo("e0");
        await Assert.That(Encoding.UTF8.GetByteCount(JudgeCiteHandles.Row(JudgeCiteHandles.Page(99_999), 999_999))).IsLessThanOrEqualTo(JudgeCiteHandles.MaxHandleBytes);
    }

    [Test]
    public async Task Minted_handles_expand_unknown_handles_drop_and_literal_refs_need_delivery() {
        var ledger = LedgerWith(new Dictionary<string, string> { ["p1.1"] = "AgentSession-r@3", ["p1.2"] = "AgentSession-r#g0t1" },
            [("AgentSession-r", 3, 6)], [("AgentSession-r", 1)]);

        var refs = JudgeCiteHandles.Expand(
            ["p1.1", "p9.9", "AgentSession-r@5", "AgentSession-r@4-6", "AgentSession-r@7", "AgentSession-r#g0t1", "AgentSession-r#g0t2", "p1.1", "not a ref"],
            ledger, max: 200, out var dropped);

        await Assert.That(refs).IsEquivalentTo(["AgentSession-r@3", "AgentSession-r@5", "AgentSession-r@4-6", "AgentSession-r#g0t1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dropped).IsEqualTo(4);
    }

    [Test]
    public async Task The_201st_distinct_ref_is_dropped() {
        var cites  = Enumerable.Range(1, 201).ToDictionary(k => $"p1.{k}", k => $"AgentSession-r@{k}");
        var ledger = LedgerWith(cites, [], []);

        var refs = JudgeCiteHandles.Expand(cites.Keys, ledger, max: 200, out var dropped);

        await Assert.That(refs.Count).IsEqualTo(200);
        await Assert.That(refs[^1]).IsEqualTo("AgentSession-r@200");
        await Assert.That(dropped).IsEqualTo(1);
    }
}
