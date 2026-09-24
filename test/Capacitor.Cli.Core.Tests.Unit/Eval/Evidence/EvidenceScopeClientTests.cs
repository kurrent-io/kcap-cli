using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The client's deadline is server-relative, so skewed clocks refresh at the same server instant; a refresh
/// happens exactly when the remaining life is at most the phase's headroom; a moved scope ends the run.</summary>
public class EvidenceScopeClientTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();
    static readonly DateTimeOffset S0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    EvidenceScopeClient Client(FakeTimeProvider time) => new(_http, _stub.Url, EvidenceServerStub.SessionId, time);

    void ServeScope(string version = "v1", string token = "tok-1") {
        _stub.FreshScope(EvidenceServerStub.Manifest(version, token, null, S0, S0.AddMinutes(30)));
        _stub.CursorPage(token, EvidenceServerStub.Manifest(version, token, null, S0, S0.AddMinutes(30)));
    }

    [Test]
    public async Task The_phase_headrooms_are_the_phases_full_inner_budgets() {
        await Assert.That(EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.Retrieval)).IsEqualTo(TimeSpan.FromSeconds(780));
        await Assert.That(EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.OneShot)).IsEqualTo(TimeSpan.FromSeconds(480));
        await Assert.That(EvidenceScopeClient.CertificationHeadroom).IsEqualTo(TimeSpan.FromSeconds(150));
        await Assert.That(EvidenceScopeClient.RetrospectiveHeadroom).IsEqualTo(TimeSpan.FromSeconds(1_020));
        await Assert.That(EvidenceScopeClient.PreDrainHeadroom).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(EvidenceScopeClient.ArtifactLifetime).IsEqualTo(TimeSpan.FromMinutes(30));
    }

    [Test]
    public async Task Clients_ten_minutes_behind_and_ahead_refresh_at_the_same_server_relative_instant() {
        ServeScope();
        var behind = new FakeTimeProvider(S0.AddMinutes(-10));
        var ahead  = new FakeTimeProvider(S0.AddMinutes(10));
        var b = Client(behind); var a = Client(ahead);
        await b.ResolveAsync(CancellationToken.None);
        await a.ResolveAsync(CancellationToken.None);

        foreach (var clock in new[] { behind, ahead }) clock.Advance(TimeSpan.FromSeconds(1_800 - 780 - 1));
        await b.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);
        await a.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);
        await Assert.That((b.Refreshes, a.Refreshes)).IsEqualTo((0, 0));

        foreach (var clock in new[] { behind, ahead }) clock.Advance(TimeSpan.FromSeconds(1));
        await b.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);
        await a.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);
        await Assert.That((b.Refreshes, a.Refreshes)).IsEqualTo((1, 1));
    }

    [Test]
    public async Task A_retrieval_question_with_781_seconds_left_runs_without_a_refresh_and_one_with_661_refreshes_first() {
        ServeScope();
        var time = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1_800 - 781));
        await Assert.That(await client.EnsureScopeAsync(EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.Retrieval), CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Ok);
        time.Advance(TimeSpan.FromSeconds(60 + 600));                                      // setup and the harness
        await Assert.That(await client.EnsureScopeAsync(EvidenceScopeClient.CertificationHeadroom, CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Ok);
        await Assert.That(client.Refreshes).IsEqualTo(1);
        await Assert.That(client.State!.Deadline - time.GetUtcNow()).IsGreaterThan(EvidenceScopeClient.CertificationHeadroom);

        var freshTime = new FakeTimeProvider(S0);
        var fresh = Client(freshTime);
        await fresh.ResolveAsync(CancellationToken.None);
        freshTime.Advance(TimeSpan.FromSeconds(1_800 - 661));
        await fresh.EnsureScopeAsync(EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.Retrieval), CancellationToken.None);
        await Assert.That(fresh.Refreshes).IsEqualTo(1);
    }

    [Test]
    public async Task The_retrospective_refreshes_under_its_1020_second_headroom_only() {
        ServeScope();
        var time = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1_800 - 1_021));
        await client.EnsureScopeAsync(EvidenceScopeClient.RetrospectiveHeadroom, CancellationToken.None);
        await Assert.That(client.Refreshes).IsEqualTo(0);
        time.Advance(TimeSpan.FromSeconds(2));
        await client.EnsureScopeAsync(EvidenceScopeClient.RetrospectiveHeadroom, CancellationToken.None);
        await Assert.That(client.Refreshes).IsEqualTo(1);
    }

    [Test]
    public async Task A_refresh_onto_another_version_is_moved_and_keeps_the_bound_state() {
        ServeScope();
        var time = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);
        _stub.FreshScope(EvidenceServerStub.Manifest("v2", "tok-2", null, S0, S0.AddMinutes(30)), priority: 1);

        time.Advance(TimeSpan.FromMinutes(20));
        var status = await client.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);

        await Assert.That(status).IsEqualTo(EvidenceScopeStatus.Moved);
        await Assert.That(client.State!.ScopeVersion).IsEqualTo("v1");
    }

    [Test]
    [Arguments(404)]
    [Arguments(409)]
    public async Task A_cursor_reopen_refused_by_the_server_is_moved(int status) {
        ServeScope();
        var client = Client(new FakeTimeProvider(S0));
        await client.ResolveAsync(CancellationToken.None);
        _stub.CursorPage("tok-1", "{}", status, priority: 1);

        await Assert.That(await client.EnsureScopeAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Moved);
        await Assert.That(client.Refreshes).IsEqualTo(0);
    }

    [Test]
    public async Task An_absent_issue_and_expiry_read_the_artifact_lifetime_from_the_send_time() {
        _stub.FreshScope(EvidenceServerStub.Manifest("v1", "tok-1", null, null, null));
        var time = new FakeTimeProvider(S0);
        var client = Client(time);

        await client.ResolveAsync(CancellationToken.None);

        await Assert.That(client.State!.Deadline).IsEqualTo(S0 + EvidenceScopeClient.ArtifactLifetime);
        await Assert.That(client.State.ServerExpiresAt).IsNull();
    }

    [Test]
    public async Task Every_manifest_page_is_followed_and_an_eleventh_page_fails() {
        _stub.FreshScope(EvidenceServerStub.Manifest("v1", "tok-1", "c1", S0, S0.AddMinutes(30), [EvidenceServerStub.Source(EvidenceServerStub.RootSource, 0, 9, 2)]));
        _stub.CursorPage("c1", EvidenceServerStub.Manifest("v1", "tok-1", "c2", S0, S0.AddMinutes(30), [EvidenceServerStub.Source("AgentSubsession-x-a1", 0, 3, null, "subagent")]));
        _stub.CursorPage("c2", EvidenceServerStub.Manifest("v1", "tok-1", null, S0, S0.AddMinutes(30), [EvidenceServerStub.Source("AgentSubsession-x-a2", 0, 3, null, "subagent")]));
        var client = Client(new FakeTimeProvider(S0));
        await Assert.That(await client.ResolveAsync(CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Ok);
        await Assert.That(client.State!.Sources.Count).IsEqualTo(3);

        using var loop = new EvidenceServerStub();
        loop.FreshScope(EvidenceServerStub.Manifest("v1", "t", "c0", S0, S0.AddMinutes(30)));
        for (var i = 0; i < 12; i++) loop.CursorPage($"c{i}", EvidenceServerStub.Manifest("v1", "t", $"c{i + 1}", S0, S0.AddMinutes(30)));
        var looping = new EvidenceScopeClient(_http, loop.Url, EvidenceServerStub.SessionId, new FakeTimeProvider(S0));
        await Assert.That(await looping.ResolveAsync(CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Failed);
    }

    [Test]
    public async Task The_certification_slice_reopens_once_whatever_the_number_of_batches() {
        ServeScope();
        var time = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);
        var entries = string.Join(",", Enumerable.Range(0, 64).Select(i => $$"""{"ref":"{{EvidenceServerStub.RootSource}}@{{i}}","state":"certified","digest":"{{new string('a', 64)}}","code":null}"""));
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{{entries}}]}""");

        await client.EnsureScopeAsync(EvidenceScopeClient.CertificationHeadroom, CancellationToken.None);
        await new EvidenceCitationClient(_http, _stub.Url, EvidenceServerStub.SessionId, time)
            .CertifyAsync("tok-1", [.. Enumerable.Range(0, 130).Select(i => $"{EvidenceServerStub.RootSource}@{i}")], CancellationToken.None);

        await Assert.That(client.Reopens).IsEqualTo(1);
        await Assert.That(_stub.Requests("evidence-citations").Count).IsEqualTo(3);
    }
}
