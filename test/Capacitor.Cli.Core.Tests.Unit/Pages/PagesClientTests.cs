using System.Net;
using System.Text;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

/// The channel's totalization: which statuses mean signed out, unavailable or unreachable, and that
/// a good body parses into the page list from the session's artefacts route.
public class PagesClientTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Body = """
        {"artefacts":[
          {"artefact_id":"a1","title":"One","owner_user_id":"u1","visibility":"private","latest_version":2,"updated_at":"2026-10-07T10:00:00Z","is_owner":true,"url":"https://server.test/a/a1"},
          {"artefact_id":"a2","title":"Two","owner_user_id":"u2","visibility":"team","latest_version":1,"updated_at":"2026-10-08T10:00:00Z","is_owner":false,"url":"https://server.test/a/a2"}]}
        """;

    sealed class Handler(HttpStatusCode status, string body = "{}", Exception? throws = null) : HttpMessageHandler {
        public Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requested = request.RequestUri;
            if (throws is not null) throw throws;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static (PagesClient Client, Handler Handler) Build(HttpStatusCode status, string body = "{}", Exception? throws = null) {
        var handler = new Handler(status, body, throws);
        return (new PagesClient(new HttpClient(handler), "https://server.test/"), handler);
    }

    [Test]
    public async Task A_good_body_reads_ready_with_every_page_from_the_sessions_route() {
        var (client, handler) = Build(HttpStatusCode.OK, Body);
        var read = await client.ListForSessionAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Pages.Select(p => p.ArtefactId)).IsEquivalentTo(new[] { "a1", "a2" });
        await Assert.That(handler.Requested!.ToString()).IsEqualTo($"https://server.test/api/sessions/{Session}/artefacts");
    }

    [Test]
    public async Task An_empty_list_is_ready_with_no_pages() {
        var (client, _) = Build(HttpStatusCode.OK, """{"artefacts":[]}""");
        var read = await client.ListForSessionAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Pages).IsEmpty();
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized, SessionPlansReadKind.SignedOut)]
    [Arguments(HttpStatusCode.Forbidden, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.NotFound, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.InternalServerError, SessionPlansReadKind.Unreachable)]
    public async Task Statuses_totalize(HttpStatusCode status, SessionPlansReadKind expected) {
        var (client, _) = Build(status);
        var read = await client.ListForSessionAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Pages).IsEmpty();
    }

    [Test]
    public async Task A_transport_failure_is_unreachable() {
        var (client, _) = Build(HttpStatusCode.OK, throws: new HttpRequestException("down"));
        var read = await client.ListForSessionAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unreachable);
    }

    [Test]
    [Arguments(".")]
    [Arguments(" ")]
    public async Task An_id_that_would_escape_the_route_is_unavailable_without_a_request(string id) {
        var (client, handler) = Build(HttpStatusCode.OK, Body);
        var read = await client.ListForSessionAsync(id, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unavailable);
        await Assert.That(handler.Requested).IsNull();
    }

    [Test]
    public async Task The_callers_cancellation_propagates() {
        var (client, _) = Build(HttpStatusCode.OK, Body);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ListForSessionAsync(Session, cts.Token));
    }
}
