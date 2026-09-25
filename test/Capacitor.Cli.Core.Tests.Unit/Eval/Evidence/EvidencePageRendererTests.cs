using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>A page is the route's body with page, has_next and row cites added and the cursor removed; its ledger record
/// locates every entry, delivers what the body delivered and keeps the continuation out of the text.</summary>
public class EvidencePageRendererTests {
    const string Src = "AgentSession-r";

    const string CallsBody = """{"scope_version":"v1","scope_complete":true,"scope_incomplete_reasons":[],"index_state":"ready","incomplete_sources":[],"incomplete_sources_truncated":false,"search_coverage":"full","results_state":"complete","partial_reason":null,"total_matches":1,"take":50,"budget_bytes":65536,"calls":[{"locator":{"ref":"AgentSession-r@7","ordinal":0},"source_id":"AgentSession-r","entry_kind":"invocation","tool":"Bash","class":"shell","is_mcp":false,"is_subagent":false,"paths":[],"paths_truncated":false,"result_status":"ok"}],"supplied":{"items":1,"bytes":null},"over_budget":false,"next_cursor":"C2"}""";

    const string AuthorizationsBody = """{"scope_version":"v1","scope_complete":true,"scope_incomplete_reasons":[],"index_state":"ready","incomplete_sources":[],"incomplete_sources_truncated":false,"total_matches":1,"take":50,"budget_bytes":65536,"authorizations":[{"authorization_ref":"AgentSession-r@8","source_id":"AgentSession-r","entry_kind":"request","request_id":"q","request_id_truncated":false,"request_id_digest":"d","outcome":"allowed"}],"supplied":{"items":1,"bytes":null},"over_budget":false,"next_cursor":null}""";

    [Test]
    public async Task An_event_page_keeps_the_body_adds_cites_and_drops_the_cursor() {
        var body = EvidenceServerStub.EventsPage(Src, [EvidenceServerStub.EventEntry(Src, 4, "first"), EvidenceServerStub.EventEntry(Src, 5, null, deferText: true)], next: "CURSOR-SECRET");

        var page = EvidencePageRenderer.Render(3, "p3", "read_events", """{"ref":"AgentSession-r@4-9"}""", body);

        using var doc = JsonDocument.Parse(page.Text);
        var root = doc.RootElement;
        await Assert.That(root.Str("page")).IsEqualTo("p3");
        await Assert.That(root.Bool("has_next")).IsTrue();
        await Assert.That(root.TryGetProperty("next_cursor", out _)).IsFalse();
        await Assert.That(page.Text.Contains("CURSOR-SECRET")).IsFalse();
        await Assert.That(root.Obj("supplied")).IsNotNull();
        var entries = root.Arr("entries")!.Value.EnumerateArray().ToList();
        await Assert.That(entries[0].Str("cite")).IsEqualTo("p3.1");
        await Assert.That(entries[1].Str("cite")).IsEqualTo("p3.2");
        await Assert.That(entries[0].Str("text")).IsEqualTo("first");
        await Assert.That(page.Cites["p3.2"]).IsEqualTo($"{Src}@5");
        await Assert.That(page.Revisions).IsEquivalentTo([(Src, 4L, 5L)]);
        await Assert.That(page.Bodies).IsEquivalentTo([($"{Src}@5", "text", (int?)null)]);
        await Assert.That(page.Source).IsEqualTo(Src);
        await Assert.That(page.HasNext).IsTrue();
        await Assert.That(page.Next).IsEqualTo("CURSOR-SECRET");
        await Assert.That(page.Bytes).IsEqualTo(Encoding.UTF8.GetByteCount(page.Text));
    }

    [Test]
    public async Task Detail_spans_locate_every_entry_in_the_delivered_text() {
        var entries = Enumerable.Range(0, 5).Select(i => EvidenceServerStub.EventEntry(Src, i, $"text ü {i} <b>")).ToList();

        var page = EvidencePageRenderer.Render(1, "p1", "read_events", "{}", EvidenceServerStub.EventsPage(Src, entries));

        var bytes = Encoding.UTF8.GetBytes(page.Text);
        await Assert.That(page.Detail.Count).IsEqualTo(5);
        foreach (var (source, revision, offset, length) in page.Detail) {
            using var span = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, offset, length));
            await Assert.That(span.RootElement.Str("ref")).IsEqualTo($"{source}@{revision}");
        }
        await Assert.That(page.Revisions).IsEquivalentTo([(Src, 0L, 4L)]);
    }

    [Test]
    public async Task A_turn_page_records_its_outline_rows_and_cites_each_turn() {
        var page = EvidencePageRenderer.Render(2, "p2", "list_turns", """{"source":"AgentSession-r"}""", EvidenceServerStub.TurnsPage(Src, [(0, 0, 4), (1, 5, 9)]));

        await Assert.That(page.Turns).IsEquivalentTo([(Src, 0), (Src, 1)]);
        await Assert.That(page.Cites["p2.1"]).IsEqualTo($"{Src}#g1t0");
        await Assert.That(page.Cites["p2.2"]).IsEqualTo($"{Src}#g1t1");
        await Assert.That(page.HasNext).IsFalse();
        await Assert.That(page.Revisions).IsEmpty();
        await Assert.That(page.Source).IsEqualTo(Src);
    }

    [Test]
    public async Task A_body_page_cites_its_root_keeps_its_offset_and_continues_by_it() {
        var page = EvidencePageRenderer.Render(4, "p4", "read_body", """{"ref":"AgentSession-r@5","field":"text"}""",
            EvidenceServerStub.BodyChunk($"{Src}@5", "text", "the opened body", nextOffset: 4096));

        using var doc = JsonDocument.Parse(page.Text);
        await Assert.That(doc.RootElement.Str("cite")).IsEqualTo("p4.1");
        await Assert.That(doc.RootElement.Num("next_offset")).IsEqualTo(4096);
        await Assert.That(page.HasNext).IsTrue();
        await Assert.That(page.Next).IsEqualTo("4096");
        await Assert.That(page.Bodies).IsEquivalentTo([($"{Src}@5", "text", (int?)null)]);
        var span = page.Detail.Single();
        await Assert.That(span.Revision).IsEqualTo(5);
        await Assert.That(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(page.Text), span.Offset, span.Length).Contains("the opened body")).IsTrue();
        await Assert.That(page.Source).IsNull();
    }

    [Test]
    public async Task Calls_and_authorizations_cite_their_row_refs_and_the_summary_cites_nothing() {
        var calls   = EvidencePageRenderer.Render(5, "p5", "list_calls", "{}", CallsBody);
        var auth    = EvidencePageRenderer.Render(6, "p6", "list_authorizations", "{}", AuthorizationsBody);
        var summary = EvidencePageRenderer.Render(7, "p7", "summarize_calls", "{}", EvidenceServerStub.Summary());

        await Assert.That(calls.Cites["p5.1"]).IsEqualTo($"{Src}@7");
        await Assert.That(calls.HasNext).IsTrue();
        await Assert.That(calls.Next).IsEqualTo("C2");
        await Assert.That(calls.Text.Contains("C2")).IsFalse();
        await Assert.That(auth.Cites["p6.1"]).IsEqualTo($"{Src}@8");
        await Assert.That(auth.HasNext).IsFalse();
        await Assert.That(summary.Cites).IsEmpty();
        await Assert.That(summary.HasNext).IsFalse();
    }

    [Test]
    public async Task The_row_ref_map_equals_the_contract_fixture() {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-evidence", "contract.json")));
        var expected = fixture.RootElement.GetProperty("row_ref_fields").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.IsNull ? null : p.Value.GetString());

        await Assert.That(EvidencePageRenderer.RowRefFields.Keys.Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(expected.Keys.Order(StringComparer.Ordinal).ToList());
        foreach (var (tool, path) in expected) await Assert.That(EvidencePageRenderer.RowRefFields[tool]).IsEqualTo(path);
    }

    [Test]
    public async Task The_sources_page_lists_at_most_fifty_rows_and_continues_through_from() {
        var sources = Enumerable.Range(0, 60).Select(i => new EvidenceRunSource($"AgentSubsession-r-a{i}", "subagent", true, 0, 9, 1)).ToList();

        var page = EvidencePageRenderer.RenderSources(1, "p1", """{"from":0}""", sources, 0);

        using var doc = JsonDocument.Parse(page.Text);
        await Assert.That(doc.RootElement.Arr("sources")!.Value.GetArrayLength()).IsEqualTo(50);
        await Assert.That(doc.RootElement.Num("next_from")).IsEqualTo(50);
        await Assert.That(doc.RootElement.Num("total")).IsEqualTo(60);
        await Assert.That(doc.RootElement.Bool("has_next")).IsTrue();
        await Assert.That(page.HasNext).IsFalse();
        await Assert.That(page.Cites).IsEmpty();

        var last = EvidencePageRenderer.RenderSources(2, "p2", """{"from":50}""", sources, 50);
        using var lastDoc = JsonDocument.Parse(last.Text);
        await Assert.That(lastDoc.RootElement.Arr("sources")!.Value.GetArrayLength()).IsEqualTo(10);
        await Assert.That(lastDoc.RootElement.Num("next_from")).IsNull();
    }
}
