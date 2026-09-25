using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The eight evidence tools send exactly one of token and cursor, return the route's body with handles and no
/// cursor, ledger every page with spans that locate its events, charge their budgets before reading arguments, keep
/// refusals correctable, end every later call after a moved scope, and re-check admission before serving cached bytes.</summary>
public class McpEvidenceJudgeToolsTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();
    readonly TempDir _tmp = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() { _http.Dispose(); _stub.Dispose(); _tmp.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;
    const string Lane = $"AgentSubsession-{EvidenceServerStub.SessionId}-a1";

    string LedgerPath => _tmp.PathTo("q1.ledger.jsonl");

    McpEvidenceJudgeTools Tools(int maxToolCalls = 48, long byteBudget = 600_000, IReadOnlyList<JudgeLedgerPage>? seeded = null) {
        var soft   = _time.GetUtcNow().AddMinutes(8);
        var header = new JudgeLedgerHeader("run", "safety/q1", "v1", new EvidenceRunBudgets(maxToolCalls, byteBudget, 65_536), soft, _time.GetUtcNow());
        using (var writer = JudgeLedgerWriter.Create(LedgerPath, header))
            foreach (var page in seeded ?? []) writer.Append(page);
        var run = new EvidenceRunFile("run", "safety/q1", EvidenceServerStub.SessionId, "v1", "tok", _time.GetUtcNow().AddMinutes(30),
            [new EvidenceRunSource(Root, "root", true, 0, 9, 2), new EvidenceRunSource(Lane, "subagent", true, 0, 4, 1)],
            header.Budgets, soft, LedgerPath, seeded ?? []);
        var runPath = _tmp.PathTo("q1.run.json");
        using (var stream = File.Create(runPath)) run.WriteTo(stream);
        _stub.CursorPage("tok", EvidenceServerStub.Manifest("v1", "tok", null, null, null));
        return McpEvidenceJudgeTools.Open(runPath, _http, _stub.Url, _time);
    }

    JudgeLedger Ledger() => JudgeLedgerReader.Read(LedgerPath);

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    static IDictionary<string, WireMock.Types.WireMockList<string>> QueryOf(WireMock.Logging.ILogEntry entry) => entry.RequestMessage.Query!;

    [Test]
    public async Task A_first_page_sends_token_and_budget_and_returns_the_body_with_handles_and_no_cursor() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 4), (1, 5, 9)], next: "N1"));
        using var tools = Tools();

        var (text, isError) = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);

        await Assert.That(isError).IsFalse();
        var query = QueryOf(_stub.Requests("evidence-turns").Single());
        await Assert.That(query["token"].Single()).IsEqualTo("tok");
        await Assert.That(query["budget_bytes"].Single()).IsEqualTo("65536");
        await Assert.That(query.ContainsKey("cursor")).IsFalse();
        using var doc = JsonDocument.Parse(text);
        await Assert.That(doc.RootElement.Str("page")).IsEqualTo("p1");
        await Assert.That(doc.RootElement.Bool("has_next")).IsTrue();
        await Assert.That(doc.RootElement.TryGetProperty("next_cursor", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("turns")[0].Str("cite")).IsEqualTo("p1.1");

        var ledger = Ledger();
        await Assert.That(ledger.Pages.Single().Text).IsEqualTo(text);
        await Assert.That(ledger.Pages.Single().Next).IsEqualTo("N1");
        await Assert.That(ledger.Calls.Single().Outcome).IsEqualTo(JudgeLedgerOutcomes.Executed);
        await Assert.That(ledger.Footer!.ToolCalls).IsEqualTo(1);
        await Assert.That(ledger.Footer.DeliveredBytes).IsEqualTo(Encoding.UTF8.GetByteCount(text));
    }

    [Test]
    public async Task Open_page_next_sends_the_cursor_alone_under_a_new_handle() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 4)], next: "N1"));
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(1, 5, 9)]), new Dictionary<string, string> { ["cursor"] = "N1" }, priority: 5);
        using var tools = Tools();

        await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);
        var (text, _) = await tools.CallAsync("open_page", Args("""{"page":"p1","next":true}"""), CancellationToken.None);

        var second = QueryOf(_stub.Requests("evidence-turns")[1]);
        await Assert.That(second["cursor"].Single()).IsEqualTo("N1");
        await Assert.That(second.ContainsKey("token")).IsFalse();
        using var doc = JsonDocument.Parse(text);
        await Assert.That(doc.RootElement.Str("page")).IsEqualTo("p2");
        var continuation = Ledger().Pages[1];
        await Assert.That(continuation.Tool).IsEqualTo("list_turns");
        await Assert.That(continuation.ArgsJson).IsEqualTo("""{"page":"p1","next":true}""");
        await Assert.That(Ledger().FollowedHandles).Contains("p1");
        await Assert.That(Ledger().Calls[1].Tool).IsEqualTo("open_page");
    }

    [Test]
    public async Task A_body_continuation_sends_token_and_offset() {
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@5", "text", "first part", nextOffset: 4096));
        using var tools = Tools();

        await tools.CallAsync("read_body", Args($$"""{"ref":"{{Root}}@5","field":"text"}"""), CancellationToken.None);
        await tools.CallAsync("open_page", Args("""{"page":"p1","next":true}"""), CancellationToken.None);

        var first  = QueryOf(_stub.Requests("evidence-body")[0]);
        var second = QueryOf(_stub.Requests("evidence-body")[1]);
        await Assert.That(first["max_bytes"].Single()).IsEqualTo("65536");
        await Assert.That(second["token"].Single()).IsEqualTo("tok");
        await Assert.That(second["offset"].Single()).IsEqualTo("4096");
        await Assert.That(second["ref"].Single()).IsEqualTo($"{Root}@5");
        await Assert.That(second.ContainsKey("cursor")).IsFalse();
    }

    [Test]
    public async Task Each_page_line_equals_the_returned_text_and_its_spans_locate_every_event() {
        var entries = Enumerable.Range(0, 4).Select(i => EvidenceServerStub.EventEntry(Root, i, $"event {i}")).ToList();
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, entries));
        using var tools = Tools();

        var (text, _) = await tools.CallAsync("read_events", Args($$"""{"ref":"{{Root}}@0-3"}"""), CancellationToken.None);

        var page  = Ledger().Pages.Single();
        var bytes = Encoding.UTF8.GetBytes(page.Text);
        await Assert.That(page.Text).IsEqualTo(text);
        await Assert.That(page.Detail.Count).IsEqualTo(4);
        foreach (var (source, revision, offset, length) in page.Detail) {
            using var span = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, offset, length));
            await Assert.That(span.RootElement.Str("ref")).IsEqualTo($"{source}@{revision}");
        }
    }

    [Test]
    public async Task A_source_outside_the_admitted_set_is_a_correctable_error_naming_the_count() {
        using var tools = Tools();

        var (text, isError) = await tools.CallAsync("list_turns", Args("""{"source":"AgentSession-someone-else"}"""), CancellationToken.None);

        await Assert.That(isError).IsTrue();
        await Assert.That(text.Contains("2 sources")).IsTrue();
        await Assert.That(_stub.Requests("evidence-turns")).IsEmpty();
        await Assert.That(Ledger().Footer!.StopReason).IsNull();
    }

    [Test]
    public async Task The_49th_call_answers_tool_call_budget_and_so_does_every_later_one() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 4)]));
        using var tools = Tools(maxToolCalls: 48);

        for (var i = 0; i < 48; i++) await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);
        var (forty9, _) = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);
        var (fifty, _)  = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);

        await Assert.That(forty9).IsEqualTo("not executed: tool_call_budget");
        await Assert.That(fifty).IsEqualTo("not executed: tool_call_budget");
        await Assert.That(_stub.Requests("evidence-turns").Count).IsEqualTo(48);
        await Assert.That(Ledger().Footer!.ToolCalls).IsEqualTo(48);
        await Assert.That(Ledger().Footer!.StopReason).IsEqualTo("tool_call_budget");
    }

    [Test]
    public async Task A_page_that_would_cross_the_byte_budget_is_withheld() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, Enumerable.Range(0, 20).Select(i => (i, (long)i, (long)i))));
        using var tools = Tools(byteBudget: 500);

        var (text, _) = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);
        var (later, _) = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);

        await Assert.That(text).IsEqualTo("not executed: byte_budget");
        await Assert.That(later).IsEqualTo("not executed: byte_budget");
        await Assert.That(Ledger().Pages).IsEmpty();
        await Assert.That(Ledger().Footer!.StopReason).IsEqualTo("byte_budget");
        await Assert.That(Ledger().Footer!.DeliveredBytes).IsEqualTo(0);
    }

    [Test]
    public async Task A_call_after_the_soft_deadline_answers_time_budget_before_its_arguments_are_read() {
        using var tools = Tools();
        _time.Advance(TimeSpan.FromMinutes(8));

        var (text, isError) = await tools.CallAsync("list_turns", Args("""{"source":"not even admitted"}"""), CancellationToken.None);

        await Assert.That(text).IsEqualTo("not executed: time_budget");
        await Assert.That(isError).IsFalse();
        await Assert.That(Ledger().Footer!.StopReason).IsEqualTo("time_budget");
        await Assert.That(Ledger().Footer!.ToolCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_bad_request_and_a_busy_index_are_correctable() {
        _stub.Route("GET", "evidence-events", 400, """{"code":"range_too_large","detail":"a range spans at most 512 events"}""");
        _stub.Route("GET", "evidence-calls", 503, """{"code":"index_busy","detail":"the evidence index is busy; retry"}""");
        using var tools = Tools();

        var (bad, badError)   = await tools.CallAsync("read_events", Args($$"""{"ref":"{{Root}}@0-9"}"""), CancellationToken.None);
        var (busy, busyError) = await tools.CallAsync("list_calls", Args("{}"), CancellationToken.None);
        var (after, _)        = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);

        await Assert.That(badError).IsTrue();
        await Assert.That(bad).IsEqualTo("Error: range_too_large — a range spans at most 512 events");
        await Assert.That(busyError).IsTrue();
        await Assert.That(busy).IsEqualTo("Error: index busy; retry");
        await Assert.That(after.StartsWith("{\"page\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Ledger().Footer!.StopReason).IsNull();
    }

    [Test]
    public async Task A_404_the_scope_re_open_confirms_is_a_refused_source_and_the_question_goes_on() {
        _stub.Route("GET", "evidence-turns", 404, "");
        using var tools = Tools();

        var (text, isError) = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Lane}}"}"""), CancellationToken.None);
        var (next, _)       = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: not in the bound scope");
        await Assert.That(next.StartsWith("{\"page\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Ledger().SourcesRefused).Contains(Lane);
        await Assert.That(Ledger().Footer!.StopReason).IsNull();
    }

    [Test]
    [Arguments(404)]
    [Arguments(409)]
    public async Task A_404_whose_re_open_fails_ends_every_later_call_as_a_moved_scope(int reopenStatus) {
        _stub.Route("GET", "evidence-turns", 404, "");
        using var tools = Tools();
        _stub.CursorPage("tok", "", reopenStatus, priority: 1);

        var (text, _)  = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);
        var (later, _) = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);

        await Assert.That(text).IsEqualTo("Error: scope moved");
        await Assert.That(later).IsEqualTo("Error: scope moved");
        await Assert.That(Ledger().Footer!.StopReason).IsEqualTo("scope_moved");
    }

    [Test]
    public async Task A_409_on_any_read_ends_every_later_call() {
        _stub.Route("GET", "evidence-events", 409, """{"code":"scope_moved","current_version":"v2"}""");
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 4)]));
        using var tools = Tools();

        var (moved, _) = await tools.CallAsync("read_events", Args($$"""{"ref":"{{Root}}@1"}"""), CancellationToken.None);
        var (later, _) = await tools.CallAsync("list_turns", Args($$"""{"source":"{{Root}}"}"""), CancellationToken.None);

        await Assert.That(moved).IsEqualTo("Error: scope moved");
        await Assert.That(later).IsEqualTo("Error: scope moved");
        await Assert.That(_stub.Requests("evidence-turns")).IsEmpty();
        await Assert.That(Ledger().Footer!.StopReason).IsEqualTo("scope_moved");
    }

    [Test]
    public async Task The_local_tools_re_open_the_token_before_serving_cached_bytes() {
        using var tools = Tools();

        var (sources, _) = await tools.CallAsync("list_sources", Args("{}"), CancellationToken.None);
        _stub.CursorPage("tok", "", 404, priority: 1);
        var (replay, _) = await tools.CallAsync("open_page", Args("""{"page":"p1"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(sources);
        await Assert.That(doc.RootElement.Arr("sources")!.Value.GetArrayLength()).IsEqualTo(2);
        await Assert.That(replay).IsEqualTo("Error: scope moved");
        await Assert.That(_stub.Requests("evidence-scope").Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_seeded_cite_handle_expands_to_its_ref_and_a_turn_to_its_event_range() {
        var outline = EvidencePageRenderer.Render(0, "o1", "list_turns", $$"""{"source":"{{Root}}"}""", EvidenceServerStub.TurnsPage(Root, [(0, 0, 4)]));
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, "e")]));
        using var tools = Tools(seeded: [outline]);

        var (_, isError) = await tools.CallAsync("read_events", Args("""{"ref":"o1.1"}"""), CancellationToken.None);

        await Assert.That(isError).IsFalse();
        await Assert.That(QueryOf(_stub.Requests("evidence-events").Single())["ref"].Single()).IsEqualTo($"{Root}@0-4");
    }

    [Test]
    public async Task Without_run_the_six_legacy_tools_are_listed_and_with_it_the_eight() {
        var legacy   = McpJudgeServer.ToolsFor(evidence: false).Select(t => t.Name).ToList();
        var evidence = McpJudgeServer.ToolsFor(evidence: true).Select(t => t.Name).ToList();

        await Assert.That(legacy).IsEquivalentTo(["get_session_recap", "get_session_errors", "get_transcript", "get_session_summary", "search_session", "get_tool_result"]);
        await Assert.That(evidence).IsEquivalentTo(["list_sources", "list_turns", "read_events", "read_body", "list_calls", "summarize_calls", "list_authorizations", "open_page"]);
        await Assert.That(McpJudgeServer.ToolsFor(evidence: true).All(t => !t.InputSchema.Properties.ContainsKey("session_id"))).IsTrue();
    }
}
