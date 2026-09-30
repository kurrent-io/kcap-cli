using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>A held scope: taken with a fresh request key, renewed through the renewal route rather than resolved again,
/// released when the client is disposed — with the latest token, and even when a later manifest page failed — and a
/// server that refuses or does not offer a hold leaves today's renewal in place.</summary>
public class EvidenceScopeHoldClientTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();
    static readonly DateTimeOffset S0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    EvidenceScopeClient Client(FakeTimeProvider time, bool holds = true) => new(_http, _stub.Url, EvidenceServerStub.SessionId, time, holds);

    static string Held(string manifest, bool held = true) => manifest[..^1] + $",\"held\":{(held ? "true" : "false")}}}";

    void ServeHold(string version = "v1", string token = "tok-1", string? next = null) {
        _stub.Route("POST", "evidence-scope/holds", 200, Held(EvidenceServerStub.Manifest(version, token, next, S0, S0.AddMinutes(30))));
        _stub.CursorPage(token, Held(EvidenceServerStub.Manifest(version, token, next, S0, S0.AddMinutes(30))));
        _stub.Route("DELETE", "evidence-scope/hold", 204, "");
    }

    void ServeRenewal(string version, string token, DateTimeOffset issued, int status = 200) =>
        _stub.Route("POST", "evidence-scope/renewal", status, status == 200 ? Held(EvidenceServerStub.Manifest(version, token, null, issued, issued.AddMinutes(30))) : "");

    string? ReleasedToken() => _stub.Requests("evidence-scope/hold").Select(e => JsonDocument.Parse(e.RequestMessage.Body!).RootElement.GetProperty("token").GetString()).SingleOrDefault();

    [Test]
    public async Task A_held_scope_is_taken_with_a_request_key_and_renewed_without_resolving_again() {
        ServeHold(); ServeRenewal("v1", "tok-2", S0.AddMinutes(18));
        var time   = new FakeTimeProvider(S0);
        var client = Client(time);

        await Assert.That(await client.ResolveAsync(CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Ok);
        var take = JsonDocument.Parse(_stub.Requests("evidence-scope/holds").Single().RequestMessage.Body!).RootElement;
        await Assert.That(take.GetProperty("request_id").GetString()!.Length).IsEqualTo(32);
        await Assert.That(take.GetProperty("continuations").GetBoolean()).IsFalse();
        await Assert.That(take.GetProperty("adopted_children").GetBoolean()).IsFalse();
        await Assert.That(client.State!.Held).IsTrue();

        time.Advance(TimeSpan.FromSeconds(1_800 - 780));
        await Assert.That(await client.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Ok);

        await Assert.That(client.Refreshes).IsEqualTo(1);
        await Assert.That(client.State.Token).IsEqualTo("tok-2");
        await Assert.That(client.State.Deadline).IsEqualTo(time.GetUtcNow() + TimeSpan.FromMinutes(30));
        await Assert.That(_stub.Requests("evidence-scope").Count(e => e.RequestMessage.Query?.ContainsKey("adopted_children") == true)).IsEqualTo(0);
    }

    [Test]
    [Arguments(200, "v2")]
    [Arguments(409, "v1")]
    [Arguments(404, "v1")]
    public async Task A_renewal_onto_another_version_or_refused_is_a_moved_scope(int status, string version) {
        ServeHold(); ServeRenewal(version, "tok-2", S0.AddMinutes(18), status);
        var time   = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1_800 - 780));
        await Assert.That(await client.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Moved);
    }

    [Test]
    public async Task Disposing_releases_the_hold_once_with_its_latest_token() {
        ServeHold(); ServeRenewal("v1", "tok-2", S0.AddMinutes(18));
        var time   = new FakeTimeProvider(S0);
        var client = Client(time);
        await client.ResolveAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1_800 - 780));
        await client.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);

        await client.DisposeAsync();
        await client.DisposeAsync();

        await Assert.That(ReleasedToken()).IsEqualTo("tok-2");
    }

    [Test]
    public async Task A_hold_whose_later_manifest_page_failed_is_still_released() {
        ServeHold(next: "page-2");
        _stub.CursorPage("page-2", "", 500);
        var client = Client(new FakeTimeProvider(S0));

        await Assert.That(await client.ResolveAsync(CancellationToken.None)).IsEqualTo(EvidenceScopeStatus.Failed);
        await client.DisposeAsync();

        await Assert.That(ReleasedToken()).IsEqualTo("tok-1");
    }

    [Test]
    public async Task A_refused_hold_is_reported_and_renews_by_resolving_again() {
        _stub.Route("POST", "evidence-scope/holds", 200, Held(EvidenceServerStub.Manifest("v1", "tok-1", null, S0, S0.AddMinutes(30)), held: false));
        _stub.CursorPage("tok-1", EvidenceServerStub.Manifest("v1", "tok-1", null, S0, S0.AddMinutes(30)));
        _stub.FreshScope(EvidenceServerStub.Manifest("v1", "tok-3", null, S0.AddMinutes(18), S0.AddMinutes(48)));
        var time   = new FakeTimeProvider(S0);
        var client = Client(time);

        await client.ResolveAsync(CancellationToken.None);
        await Assert.That(client.HoldRefused).IsTrue();
        await Assert.That(client.State!.Held).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1_800 - 780));
        await client.EnsureScopeAsync(TimeSpan.FromSeconds(780), CancellationToken.None);
        await Assert.That(client.State.Token).IsEqualTo("tok-3");
        await Assert.That(_stub.Requests("evidence-scope/renewal")).IsEmpty();
        await client.DisposeAsync();
        await Assert.That(_stub.Requests("evidence-scope/hold")).IsEmpty();
    }

    [Test]
    public async Task Without_holds_the_client_resolves_and_releases_nothing_as_before() {
        _stub.FreshScope(EvidenceServerStub.Manifest("v1", "tok-1", null, S0, S0.AddMinutes(30)));
        var client = Client(new FakeTimeProvider(S0), holds: false);

        await client.ResolveAsync(CancellationToken.None);
        await client.DisposeAsync();

        await Assert.That(_stub.Requests("evidence-scope/holds")).IsEmpty();
        await Assert.That(_stub.Requests("evidence-scope/hold")).IsEmpty();
        await Assert.That(client.State!.Held).IsFalse();
    }

    sealed class ThrowingOnDelete : DelegatingHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Delete ? throw new InvalidOperationException("credential store unavailable") : base.SendAsync(request, cancellationToken);
    }

    [Test]
    public async Task A_release_that_throws_anything_is_contained() {
        ServeHold();
        using var http = new HttpClient(new ThrowingOnDelete { InnerHandler = new HttpClientHandler() });
        var client = new EvidenceScopeClient(http, _stub.Url, EvidenceServerStub.SessionId, new FakeTimeProvider(S0), holds: true);
        await client.ResolveAsync(CancellationToken.None);

        await client.DisposeAsync();

        await Assert.That(client.LastError).Contains("credential store unavailable");
    }
}
