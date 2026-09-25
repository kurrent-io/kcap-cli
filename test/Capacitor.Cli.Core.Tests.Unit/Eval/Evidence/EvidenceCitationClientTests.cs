using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Batches of at most 64, work_budget refs resent, a 404/409 or an answer under another scope version run-fatal
/// with nothing certified, and refs the shared 90 s budget leaves dropped and counted.</summary>
public class EvidenceCitationClientTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();
    static readonly string Digest = new('b', 64);

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Certified(IEnumerable<int> revs) => string.Join(",", revs.Select(i => $$"""{"ref":"{{EvidenceServerStub.RootSource}}@{{i}}","state":"certified","digest":"{{Digest}}","code":null}"""));

    EvidenceCitationClient Client(TimeProvider? time = null) => new(_http, _stub.Url, EvidenceServerStub.SessionId, time ?? TimeProvider.System);

    [Test]
    public async Task Seventy_refs_go_in_two_requests_and_come_back_in_request_order() {
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{{Certified(Enumerable.Range(0, 70))}}]}""");

        var outcome = await Client().CertifyAsync("tok", "v1", [.. Enumerable.Range(0, 70).Select(i => $"{EvidenceServerStub.RootSource}@{i}")], CancellationToken.None);

        await Assert.That(_stub.Requests("evidence-citations").Count).IsEqualTo(2);
        await Assert.That(outcome.ScopeLost).IsFalse();
        await Assert.That(outcome.Certified.Select(c => c.Ref)).IsEquivalentTo(Enumerable.Range(0, 70).Select(i => $"{EvidenceServerStub.RootSource}@{i}").ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(outcome.Dropped).IsEqualTo(0);
    }

    [Test]
    public async Task A_work_budget_ref_is_resent_and_certified_later() {
        var r0 = $"{EvidenceServerStub.RootSource}@0"; var r1 = $"{EvidenceServerStub.RootSource}@1";
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{"ref":"{{r0}}","state":"certified","digest":"{{Digest}}","code":null},{"ref":"{{r1}}","state":"refused","digest":null,"code":"work_budget"}]}""");
        _stub.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath($"/api/sessions/{EvidenceServerStub.SessionId}/evidence-citations").WithBody(b => b is not null && !b.Contains(r0)).UsingPost())
            .AtPriority(1).RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBody($$"""{"scope_version":"v1","citations":[{"ref":"{{r1}}","state":"certified","digest":"{{Digest}}","code":null}]}"""));

        var outcome = await Client().CertifyAsync("tok", "v1", [r0, r1], CancellationToken.None);

        await Assert.That(outcome.Certified.Select(c => c.Ref)).IsEquivalentTo([r0, r1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_stub.Requests("evidence-citations").Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(404)]
    [Arguments(409)]
    public async Task A_whole_request_refusal_is_scope_loss_with_nothing_certified(int status) {
        _stub.Route("POST", "evidence-citations", status, "{}");

        var outcome = await Client().CertifyAsync("tok", "v1", [$"{EvidenceServerStub.RootSource}@0"], CancellationToken.None);

        await Assert.That(outcome.ScopeLost).IsTrue();
        await Assert.That(outcome.Certified).IsEmpty();
    }

    [Test]
    public async Task An_answer_under_another_scope_version_is_scope_loss_with_nothing_certified() {
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v2","citations":[{{Certified([0])}}]}""");

        var outcome = await Client().CertifyAsync("tok", "v1", [$"{EvidenceServerStub.RootSource}@0"], CancellationToken.None);

        await Assert.That(outcome.ScopeLost).IsTrue();
        await Assert.That(outcome.Certified).IsEmpty();
    }

    [Test]
    public async Task Refs_the_budget_leaves_are_dropped_and_counted() {
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{{Certified([0])}}]}""", delay: TimeSpan.FromSeconds(5));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var running = Client(time).CertifyAsync("tok", "v1", [.. Enumerable.Range(0, 3).Select(i => $"{EvidenceServerStub.RootSource}@{i}")], CancellationToken.None);
        await Task.Delay(200);
        time.Advance(EvidenceCitationClient.CertificationBudget + TimeSpan.FromSeconds(1));
        var outcome = await running;

        await Assert.That(outcome.ScopeLost).IsFalse();
        await Assert.That(outcome.Certified).IsEmpty();
        await Assert.That(outcome.Dropped).IsEqualTo(3);
    }
}
