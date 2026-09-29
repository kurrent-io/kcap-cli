using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Route choice is the server's fit test over the wire: F is the smaller bound, a scope that cannot fit is decided without a
/// read, a fitting trace carries every canonical body with its cites and entry spans, an oversized one stops on the page that
/// crosses F, a non-text body routes to retrieval, and a failed read is reported rather than routed.</summary>
public class EvidenceRouteChoiceTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;

    EvidenceTraceAssembler Assembler() => new(new EvidenceReadClient(_http, _stub.Url, EvidenceServerStub.SessionId), "tok", 65_536);

    static EvidenceSourceDto Source(long cutoff) => new() {
        SourceId = Root, Kind = "root", SessionId = EvidenceServerStub.SessionId, FirstRevision = 0, RevisionCutoff = cutoff, TurnCount = 1, Availability = "available"
    };

    static Dictionary<string, string> Ref(string reference) => new() { ["ref"] = reference };

    [Test]
    public async Task F_is_the_smaller_of_the_advertised_limit_and_four_times_the_token_budget() {
        await Assert.That(EvidenceRouteChoice.OneShotLimitChars(400_000, 200_000)).IsEqualTo(400_000);
        await Assert.That(EvidenceRouteChoice.OneShotLimitChars(400_000, 50_000)).IsEqualTo(200_000);
        await Assert.That(EvidenceRouteChoice.OneShotLimitChars(400_000, int.MaxValue)).IsEqualTo(400_000);
    }

    [Test]
    public async Task A_scope_whose_event_count_cannot_fit_is_decided_without_a_read() {
        var (route, trace) = await EvidenceRouteChoice.ChooseAsync(Assembler(), [Source(9_999)], 100_000, CancellationToken.None);

        await Assert.That(route).IsEqualTo(EvidenceRoute.Retrieval);
        await Assert.That(trace.Reads).IsEqualTo(0);
        await Assert.That(_stub.Requests("evidence-events")).IsEmpty();
    }

    [Test]
    public async Task A_fitting_trace_is_one_shot_with_its_bodies_cites_and_entry_spans() {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, "hello"), EvidenceServerStub.EventEntry(Root, 1, null, deferText: true)]), Ref($"{Root}@0-1"));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@1", "text", "a long body"));

        var (route, trace) = await EvidenceRouteChoice.ChooseAsync(Assembler(), [Source(1)], 400_000, CancellationToken.None);

        await Assert.That(route).IsEqualTo(EvidenceRoute.OneShot);
        await Assert.That(trace.Fits).IsTrue();
        await Assert.That(trace.FailedStatus).IsNull();
        await Assert.That(trace.Cites["e0"]).IsEqualTo($"{Root}@0");
        await Assert.That(trace.Cites["e1"]).IsEqualTo($"{Root}@1");
        await Assert.That(trace.Chars).IsEqualTo(trace.TraceJson.Length);
        await Assert.That(trace.TotalChars).IsEqualTo(trace.TraceJson.Length);

        using var doc = JsonDocument.Parse(trace.TraceJson);
        var section = doc.RootElement[0];
        await Assert.That(section.Str("source")).IsEqualTo(Root);
        await Assert.That(section.GetProperty("entries")[1].Str("text")).IsEqualTo("a long body");

        var bytes = Encoding.UTF8.GetBytes(trace.TraceJson);
        foreach (var (source, revision, offset, length) in trace.Detail) {
            using var span = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, offset, length));
            await Assert.That(span.RootElement.Str("ref")).IsEqualTo($"{source}@{revision}");
        }
        await Assert.That(trace.Detail.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_oversized_trace_stops_on_the_page_that_crosses_the_limit() {
        var big = new string('x', 3_000);
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, big), EvidenceServerStub.EventEntry(Root, 1, big)], next: "c1"), Ref($"{Root}@0-5"));
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 2, big), EvidenceServerStub.EventEntry(Root, 3, big)], next: "c2"), Ref($"{Root}@2-5"));
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 4, big), EvidenceServerStub.EventEntry(Root, 5, big)]), Ref($"{Root}@4-5"));

        var (route, trace) = await EvidenceRouteChoice.ChooseAsync(Assembler(), [Source(5)], 8_000, CancellationToken.None);

        await Assert.That(route).IsEqualTo(EvidenceRoute.Retrieval);
        await Assert.That(trace.Fits).IsFalse();
        await Assert.That(trace.Reads).IsEqualTo(2);
        await Assert.That(_stub.Requests("evidence-events").Count).IsEqualTo(2);
        await Assert.That(trace.TraceJson).IsEmpty();
        await Assert.That(trace.TotalChars).IsGreaterThan(8_000);
    }

    [Test]
    public async Task A_body_that_is_not_utf8_text_routes_to_retrieval() {
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, null, deferText: true)]));
        _stub.Route("GET", "evidence-body", 200, EvidenceServerStub.BodyChunk($"{Root}@0", "text", "AAEC", encoding: "base64"));

        var (route, trace) = await EvidenceRouteChoice.ChooseAsync(Assembler(), [Source(0)], 400_000, CancellationToken.None);

        await Assert.That(route).IsEqualTo(EvidenceRoute.Retrieval);
        await Assert.That(trace.FailedStatus).IsNull();
    }

    [Test]
    public async Task A_moved_scope_during_the_fit_test_is_reported_not_routed() {
        _stub.Route("GET", "evidence-events", 409, """{"code":"scope_moved","current_version":"v2"}""");

        var (_, trace) = await EvidenceRouteChoice.ChooseAsync(Assembler(), [Source(3)], 400_000, CancellationToken.None);

        await Assert.That(trace.FailedStatus).IsEqualTo(409);
        await Assert.That(trace.Fits).IsFalse();
    }
}
