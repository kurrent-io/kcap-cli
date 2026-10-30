using System.Net;
using System.Text;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

/// The channel's totalization: which statuses mean signed out, unavailable or unreachable, and that
/// a good body parses into the pages the server listed, on the session's own route.
public class SessionPagesClientTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Body = """
        {"artefacts":[{"artefact_id":"p1","title":"Findings","owner_user_id":"u1","visibility":"org","latest_version":3,"updated_at":"2026-10-30T10:00:00Z","is_owner":false,"url":"https://kcap.test/pages/p1"}]}
        """;

    sealed class Handler(HttpStatusCode status, string body = "{}") : HttpMessageHandler {
        public Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static (SessionPagesClient Client, Handler Handler) Build(HttpStatusCode status, string body = "{}") {
        var handler = new Handler(status, body);
        return (new SessionPagesClient(new HttpClient(handler), "https://server.test/"), handler);
    }

    [Test]
    public async Task A_good_body_reads_ready_from_the_sessions_artefacts_route() {
        var (client, handler) = Build(HttpStatusCode.OK, Body);

        var read = await client.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        var page = read.Pages.Single();
        await Assert.That(page.Title).IsEqualTo("Findings");
        await Assert.That(page.Url).IsEqualTo("https://kcap.test/pages/p1");
        await Assert.That(handler.Requested!.ToString()).IsEqualTo($"https://server.test/api/sessions/{Session}/artefacts");
    }

    [Test]
    public async Task An_empty_listing_is_ready_with_no_pages() {
        var (client, _) = Build(HttpStatusCode.OK, """{"artefacts":[]}""");

        var read = await client.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Pages).IsEmpty();
    }

    /// 404 is what the server answers for a session this viewer may not see, and what a server
    /// without the route answers; neither may read as an empty list.
    [Test]
    [Arguments(HttpStatusCode.Unauthorized, SessionPlansReadKind.SignedOut)]
    [Arguments(HttpStatusCode.Forbidden, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.NotFound, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.BadGateway, SessionPlansReadKind.Unreachable)]
    public async Task Statuses_totalize(HttpStatusCode status, SessionPlansReadKind expected) {
        var (client, _) = Build(status);

        var read = await client.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Pages).IsEmpty();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("<html>sign in</html>")]
    public async Task A_body_that_is_not_a_listing_is_unreachable(string body) {
        var (client, _) = Build(HttpStatusCode.OK, body);

        var read = await client.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unreachable);
    }

    [Test]
    [Arguments(".")]
    [Arguments(" ")]
    public async Task An_id_that_would_escape_the_route_is_unavailable_without_a_request(string id) {
        var (client, handler) = Build(HttpStatusCode.OK, Body);

        var read = await client.ReadAsync(id, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unavailable);
        await Assert.That(handler.Requested).IsNull();
    }

    [Test]
    public async Task The_callers_cancellation_propagates() {
        var (client, _) = Build(HttpStatusCode.OK, Body);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReadAsync(Session, cts.Token));
    }
}
