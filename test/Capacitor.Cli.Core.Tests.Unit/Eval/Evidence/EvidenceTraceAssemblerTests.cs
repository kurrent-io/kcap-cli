using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>A tool call's arguments land in the trace exactly where the entry's own inline arguments would: nested under
/// that call's own <c>arguments</c>, whether they arrived inline or were resolved from a deferred body — never as a
/// sibling key next to <c>calls</c>.</summary>
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
}
