using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The CLI's coverage vocabulary, failure codes, cite grammar and canonical-content table equal the golden fixture the
/// server asserts S5 against, so the two producers cannot drift apart silently.</summary>
public class EvidenceContractFixtureTests {
    static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-evidence", "contract.json"))).RootElement;

    static List<string> Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal)];

    static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    [Test]
    public async Task The_vocabularies_failure_codes_and_citation_cap_match() {
        await Assert.That(Fixture.GetProperty("coverage_policy_version").GetString()).IsEqualTo(EvalService.EvidenceCoveragePolicyVersion);
        await Assert.That(Strings(Fixture.GetProperty("omission_kinds"))).IsEquivalentTo(Sorted(EvalOmissionKinds.All));
        await Assert.That(Strings(Fixture.GetProperty("stop_reasons"))).IsEquivalentTo(Sorted(EvalStopReasons.All));
        await Assert.That(Strings(Fixture.GetProperty("cli_failure_codes"))).IsEquivalentTo(Sorted(EvalFailureCodes.All));
        await Assert.That(Fixture.GetProperty("max_citations").GetInt32()).IsEqualTo(EvidenceBudgets.MaxCitations);
        await Assert.That(Fixture.GetProperty("max_citations").GetInt32()).IsEqualTo(EvalEvidenceCoverage.MaxCitations);
    }

    [Test]
    public async Task Minted_handles_match_the_grammar_and_the_byte_limit() {
        var cite     = Fixture.GetProperty("cite");
        var page     = new Regex(cite.GetProperty("page_handle").GetString()!);
        var row      = new Regex(cite.GetProperty("row_handle").GetString()!);
        var oneShot  = new Regex(cite.GetProperty("one_shot_handle").GetString()!);
        var maxBytes = cite.GetProperty("max_handle_bytes").GetInt32();

        await Assert.That(maxBytes).IsEqualTo(JudgeCiteHandles.MaxHandleBytes);
        await Assert.That(maxBytes).IsEqualTo(EvidenceBudgets.MaxCiteHandleBytes);
        foreach (var handle in new[] { JudgeCiteHandles.Page(1), JudgeCiteHandles.Page(99_999), JudgeCiteHandles.Seeded(0), JudgeCiteHandles.Seeded(3) })
            await Assert.That(page.IsMatch(handle)).IsTrue();
        foreach (var handle in new[] { JudgeCiteHandles.Row(JudgeCiteHandles.Page(1), 1), JudgeCiteHandles.Row(JudgeCiteHandles.Page(99_999), 9_999), JudgeCiteHandles.Row(JudgeCiteHandles.Seeded(0), 50) }) {
            await Assert.That(row.IsMatch(handle)).IsTrue();
            await Assert.That(Encoding.UTF8.GetByteCount(handle)).IsLessThanOrEqualTo(maxBytes);
        }
        foreach (var handle in new[] { JudgeCiteHandles.OneShot(0), JudgeCiteHandles.OneShot(123_456) })
            await Assert.That(oneShot.IsMatch(handle)).IsTrue();
        await Assert.That(page.IsMatch("p01")).IsFalse();
        await Assert.That(row.IsMatch("p1.0")).IsFalse();
    }

    [Test]
    public async Task The_canonical_content_table_matches_for_every_entry_kind() {
        var table = Fixture.GetProperty("canonical_bodies");
        foreach (var kind in table.EnumerateObject()) {
            using var entry = JsonDocument.Parse(FullyDeferred(kind.Name));
            var fields = EvidenceCanonicalContent.Deferred(entry.RootElement).Select(b => b.Field).Distinct().Order(StringComparer.Ordinal).ToList();
            await Assert.That(fields).IsEquivalentTo(Strings(kind.Value));
            await Assert.That(EvidenceCanonicalContent.IsContentKind(kind.Name)).IsEqualTo(!Strings(kind.Value).SequenceEqual(["payload"]));
        }

        await Assert.That(Strings(Fixture.GetProperty("shared_body_key_fields"))).IsEquivalentTo(["arguments", "call"]);
        await Assert.That(EvidenceCanonicalContent.BodyKey("s@1", "arguments", 0)).IsEqualTo(EvidenceCanonicalContent.BodyKey("s@1", "call", 0));
        await Assert.That(EvidenceCanonicalContent.BodyKey("s@1", "text", null)).IsNotEqualTo(EvidenceCanonicalContent.BodyKey("s@1", "output", null));
    }

    [Test]
    public async Task An_inline_field_leaves_nothing_deferred() {
        const string inline = """{"ref":"AgentSession-r@1","revision":1,"event_type":"E","kind":"assistant_text","text":"hi","text_body":{"field":"text","ordinal":null,"bytes":2,"ref":"AgentSession-r@1"},"payload_body":{"field":"payload","ordinal":null,"bytes":9,"ref":"AgentSession-r@1"}}""";
        using var entry = JsonDocument.Parse(inline);
        await Assert.That(EvidenceCanonicalContent.Deferred(entry.RootElement)).IsEmpty();
    }

    // Every body the entry can defer is left as a descriptor: no inline text, output or arguments, and one call past the listed ones.
    static string FullyDeferred(string kind) {
        const string r = "AgentSession-r@1";
        static string D(string field, int? ordinal, long bytes) =>
            $$"""{"field":"{{field}}","ordinal":{{ordinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}},"bytes":{{bytes}},"ref":"AgentSession-r@1" }""";
        return $$"""{"ref":"{{r}}","revision":1,"event_type":"E","kind":"{{kind}}","payload_body":{{D("payload", null, 10)}},"metadata_body":{{D("metadata", null, 2)}},"text_body":{{D("text", null, 20000)}},"output_body":{{D("output", null, 20000)}},"calls":[{"ordinal":0,"arguments_body":{{D("arguments", 0, 20000)}},"call_body":{{D("call", 0, 20100)}} }],"calls_total":2 }""";
    }
}
