using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>A resolved arguments body nests under its own call's <c>arguments</c>, never as a sibling of <c>calls</c>.</summary>
public class EvidenceTraceAssemblerTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;

    EvidenceTraceAssembler Assembler() => new(new EvidenceReadClient(_http, _stub.Url, EvidenceServerStub.SessionId), "tok", 65_536);

    static List<EvidenceSourceDto> Source(long cutoff) => [new() {
        SourceId = Root, Kind = "root", SessionId = EvidenceServerStub.SessionId, FirstRevision = 0, RevisionCutoff = cutoff, TurnCount = 1, Availability = "available"
    }];

    static JsonElement FirstCall(string traceJson) =>
        JsonDocument.Parse(traceJson).RootElement[0].GetProperty("entries")[0].GetProperty("calls")[0];

    [Test]
    public async Task Inline_arguments_are_carried_through_unchanged() {
        var call  = EvidenceServerStub.InlineCall(0, "Bash", """{"cmd":"ls"}""");
        var entry = EvidenceServerStub.ToolCallEntry(Root, 0, [call]);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [entry]));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, CancellationToken.None);

        await Assert.That(trace.Fits).IsTrue();
        var arguments = FirstCall(trace.TraceJson).GetProperty("arguments");
        await Assert.That(arguments.GetProperty("cmd").GetString()).IsEqualTo("ls");
    }

    [Test]
    public async Task Deferred_arguments_resolve_under_the_calls_own_arguments_field() {
        var call  = EvidenceServerStub.DeferredCall(Root, 0, 0, "Bash");
        var entry = EvidenceServerStub.ToolCallEntry(Root, 0, [call]);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [entry]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "arguments", """{"cmd":"rm -rf /"}"""));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, CancellationToken.None);

        await Assert.That(trace.Fits).IsTrue();
        var entryElement = JsonDocument.Parse(trace.TraceJson).RootElement[0].GetProperty("entries")[0];
        await Assert.That(entryElement.TryGetProperty("arguments[0]", out _)).IsFalse();
        var arguments = FirstCall(trace.TraceJson).GetProperty("arguments");
        await Assert.That(arguments.IsObject).IsTrue();
        await Assert.That(arguments.GetProperty("cmd").GetString()).IsEqualTo("rm -rf /");
    }

    [Test]
    public async Task A_mixed_entry_resolves_only_the_deferred_call_and_leaves_the_inline_one_alone() {
        var inline   = EvidenceServerStub.InlineCall(0, "Bash", """{"cmd":"ls"}""");
        var deferred = EvidenceServerStub.DeferredCall(Root, 0, 1, "Read");
        var entry    = EvidenceServerStub.ToolCallEntry(Root, 0, [inline, deferred]);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [entry]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "arguments", """{"path":"a.txt"}"""));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, CancellationToken.None);

        await Assert.That(trace.Fits).IsTrue();
        var calls = JsonDocument.Parse(trace.TraceJson).RootElement[0].GetProperty("entries")[0].GetProperty("calls");
        await Assert.That(calls.GetArrayLength()).IsEqualTo(2);
        await Assert.That(calls[0].GetProperty("arguments").GetProperty("cmd").GetString()).IsEqualTo("ls");
        await Assert.That(calls[1].GetProperty("arguments").GetProperty("path").GetString()).IsEqualTo("a.txt");
    }

    // Bounds every call, so a loop that never ends fails the test instead of hanging it.
    static CancellationToken Bounded() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Test]
    [Arguments("", 0L)]
    [Arguments("", 7L)]
    [Arguments("abc", 0L)]
    public async Task A_body_chunk_that_does_not_advance_is_a_failed_read(string content, long nextOffset) {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, null, deferText: true)]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "text", content, nextOffset: nextOffset));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, Bounded());

        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.FailedStatus).IsEqualTo(200);
    }

    [Test]
    [Arguments(0L, 2)]
    [Arguments(9L, 1)]
    [Arguments(long.MaxValue, 1)]
    public async Task An_events_page_whose_revisions_leave_the_requested_range_is_a_failed_read(long revision, int reads) {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, revision, "hi")], next: "more"));

        var trace = await Assembler().AssembleAsync(Source(5), 4_000, Bounded());

        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.FailedStatus).IsEqualTo(200);
        await Assert.That(trace.Reads).IsEqualTo(reads);
    }

    [Test]
    [Arguments("evidence-events")]
    [Arguments("evidence-body")]
    public async Task A_successful_read_whose_body_does_not_parse_is_a_failed_read(string unreadable) {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, null, deferText: true)]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "text", "hi"));
        _stub.Route("GET", unreadable, 200, "<html>not json</html>", priority: 1);

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, Bounded());

        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.FailedStatus).IsEqualTo(200);
    }

    [Test]
    public async Task Deferred_arguments_that_are_not_json_are_a_failed_read() {
        var entry = EvidenceServerStub.ToolCallEntry(Root, 0, [EvidenceServerStub.DeferredCall(Root, 0, 0, "Bash")]);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [entry]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "arguments", "{\"cmd\":"));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, Bounded());

        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.FailedStatus).IsEqualTo(200);
    }

    [Test]
    public async Task Null_inline_arguments_beside_a_body_resolve_to_that_body() {
        var call  = $"{{\"ordinal\":0,\"tool\":\"Bash\",\"arguments\":null,\"arguments_body\":{EvidenceServerStub.Descriptor($"{Root}@0", "arguments", 40, 0)}}}";
        var entry = EvidenceServerStub.ToolCallEntry(Root, 0, [call]);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [entry]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "arguments", """{"cmd":"ls"}"""));

        var trace = await Assembler().AssembleAsync(Source(0), 400_000, Bounded());

        await Assert.That(trace.Fits).IsTrue();
        await Assert.That(FirstCall(trace.TraceJson).GetProperty("arguments").GetProperty("cmd").GetString()).IsEqualTo("ls");
    }

    [Test]
    public async Task An_empty_events_page_with_a_continuation_is_a_failed_read() {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [], next: "more"));

        var trace = await Assembler().AssembleAsync(Source(5), 400_000, Bounded());

        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.FailedStatus).IsEqualTo(200);
        await Assert.That(trace.Reads).IsEqualTo(1);
    }
}
