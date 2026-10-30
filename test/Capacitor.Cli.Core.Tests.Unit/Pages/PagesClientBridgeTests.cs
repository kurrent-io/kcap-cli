using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Pages;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

/// Content reads pin a version after the detail names it; bridge calls return the server's status
/// and body uninterpreted; results access follows the web viewer's owner-or-shared rule.
public class PagesClientBridgeTests {
    const string Detail = """{"artefact":{"artefact_id":"a1","title":"One","owner_user_id":"u1","visibility":"team","latest_version":3,"updated_at":"2026-10-07T10:00:00Z","is_owner":false,"url":"https://server.test/a/a1"}}""";
    const string OwnerDetail = """{"artefact":{"artefact_id":"a1","title":"One","owner_user_id":"u1","visibility":"team","latest_version":3,"updated_at":"2026-10-07T10:00:00Z","is_owner":true,"url":"https://server.test/a/a1"}}""";

    sealed class Handler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> answer, Exception? throws = null) : HttpMessageHandler {
        public readonly List<(HttpMethod Method, string Uri, string? Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            if (throws is not null) throw throws;
            var (status, text) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static (PagesClient Client, Handler Handler) Build(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> answer, Exception? throws = null) {
        var handler = new Handler(answer, throws);
        return (new PagesClient(new HttpClient(handler), "https://server.test/"), handler);
    }

    static (PagesClient Client, Handler Handler) Page(string detail = Detail, string html = "<p>hi</p>", HttpStatusCode detailStatus = HttpStatusCode.OK, HttpStatusCode contentStatus = HttpStatusCode.OK) =>
        Build((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal) ? (contentStatus, html) : (detailStatus, detail));

    [Test]
    public async Task Content_reads_the_latest_version_named_by_the_detail_in_the_dark_theme() {
        var (client, handler) = Page();
        var read = await client.ReadContentAsync("a1", null, CancellationToken.None);
        var found = (PageContentRead.Found)read;
        await Assert.That(found.Version).IsEqualTo(3);
        await Assert.That(found.Html).IsEqualTo("<p>hi</p>");
        await Assert.That(found.Page.Title).IsEqualTo("One");
        await Assert.That(handler.Requests.Select(r => r.Uri)).IsEquivalentTo(new[] { "/api/artefacts/a1", "/artefacts/a1/content?v=3&theme=dark" });
    }

    [Test]
    public async Task An_explicit_version_is_read_instead_of_the_latest() {
        var (client, handler) = Page();
        var read = await client.ReadContentAsync("a1", 2, CancellationToken.None);
        await Assert.That(((PageContentRead.Found)read).Version).IsEqualTo(2);
        await Assert.That(handler.Requests[1].Uri).IsEqualTo("/artefacts/a1/content?v=2&theme=dark");
    }

    [Test]
    public async Task A_missing_detail_is_not_found_and_asks_for_no_content() {
        var (client, handler) = Page(detailStatus: HttpStatusCode.NotFound);
        await Assert.That(await client.ReadContentAsync("a1", null, CancellationToken.None)).IsTypeOf<PageContentRead.NotFound>();
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(HttpStatusCode.Forbidden, true)]
    [Arguments(HttpStatusCode.NotFound, true)]
    [Arguments(HttpStatusCode.Forbidden, false)]
    [Arguments(HttpStatusCode.Unauthorized, false)]
    [Arguments(HttpStatusCode.ServiceUnavailable, false)]
    public async Task Failing_statuses_totalize_on_either_request(HttpStatusCode status, bool onContent) {
        var (client, _) = onContent ? Page(contentStatus: status) : Page(detailStatus: status);
        var read = await client.ReadContentAsync("a1", null, CancellationToken.None);
        await Assert.That(read.GetType()).IsEqualTo(status switch {
            HttpStatusCode.Unauthorized => typeof(PageContentRead.SignedOut),
            HttpStatusCode.ServiceUnavailable => typeof(PageContentRead.Unreachable),
            _ => typeof(PageContentRead.NotFound),
        });
    }

    [Test]
    public async Task Content_with_a_transport_failure_is_unreachable() {
        var (client, _) = Build((_, _) => (HttpStatusCode.OK, "{}"), new HttpRequestException("down"));
        await Assert.That(await client.ReadContentAsync("a1", null, CancellationToken.None)).IsTypeOf<PageContentRead.Unreachable>();
    }

    [Test]
    public async Task Content_propagates_the_callers_cancellation() {
        var (client, _) = Build((_, _) => (HttpStatusCode.OK, "{}"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await client.ReadContentAsync("a1", null, cts.Token));
    }

    [Test]
    public async Task Submit_posts_the_version_and_payload() {
        var (client, handler) = Build((_, _) => (HttpStatusCode.OK, """{"ok":true}"""));
        using var payload = JsonDocument.Parse("""{"choice":"b"}""");
        var reply = await client.SubmitAsync("a1", 3, payload.RootElement, CancellationToken.None);
        await Assert.That(reply.Status).IsEqualTo(200);
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Uri).IsEqualTo("/api/artefacts/a1/responses");
        await Assert.That(handler.Requests[0].Body).IsEqualTo("""{"version":3,"payload":{"choice":"b"}}""");
    }

    [Test]
    public async Task A_conflict_keeps_its_status_and_the_servers_body() {
        var (client, _) = Build((_, _) => (HttpStatusCode.Conflict, """{"error":"closed"}"""));
        using var payload = JsonDocument.Parse("{}");
        var reply = await client.SubmitAsync("a1", 3, payload.RootElement, CancellationToken.None);
        await Assert.That(reply.Status).IsEqualTo(409);
        await Assert.That(reply.Body!.Value.GetProperty("error").GetString()).IsEqualTo("closed");
    }

    [Test]
    public async Task A_transport_failure_is_status_zero_with_no_body() {
        var (client, _) = Build((_, _) => (HttpStatusCode.OK, "{}"), new HttpRequestException("down"));
        var reply = await client.ResultsAsync("a1", 3, CancellationToken.None);
        await Assert.That(reply.Status).IsEqualTo(0);
        await Assert.That(reply.Body).IsNull();
    }

    [Test]
    public async Task Results_and_mine_carry_the_version_in_the_query() {
        var (client, handler) = Build((_, _) => (HttpStatusCode.OK, "{}"));
        await client.ResultsAsync("a1", 3, CancellationToken.None);
        await client.MineAsync("a1", 3, CancellationToken.None);
        await Assert.That(handler.Requests.Select(r => r.Uri)).IsEquivalentTo(new[] { "/api/artefacts/a1/results?version=3", "/api/artefacts/a1/responses?version=3&mine=true" });
    }

    [Test]
    public async Task An_unparseable_body_is_null_not_an_error() {
        var (client, _) = Build((_, _) => (HttpStatusCode.BadGateway, "<html>"));
        var reply = await client.ResultsAsync("a1", 3, CancellationToken.None);
        await Assert.That(reply.Status).IsEqualTo(502);
        await Assert.That(reply.Body).IsNull();
    }

    static async Task<(PageResultsAccess Access, Handler Handler)> Access(string detail, HttpStatusCode status, string body, Exception? throws = null) {
        var (client, handler) = Build((_, _) => (status, body), throws);
        var page = System.Text.Json.JsonSerializer.Deserialize(detail, CapacitorJsonContext.Default.ArtefactDetailDto)!.Artefact;
        return (await client.ResultsAccessAsync("a1", page, CancellationToken.None), handler);
    }

    [Test]
    public async Task The_owner_sees_results_without_a_request() {
        var (access, handler) = await Access(OwnerDetail, HttpStatusCode.OK, """{"mode":"owner"}""");
        await Assert.That(access).IsEqualTo(PageResultsAccess.Visible);
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task A_viewer_sees_results_unless_they_are_owner_only() {
        var (shared, handler) = await Access(Detail, HttpStatusCode.OK, """{"mode":"aggregate"}""");
        await Assert.That(shared).IsEqualTo(PageResultsAccess.Visible);
        await Assert.That(handler.Requests[0].Uri).IsEqualTo("/api/artefacts/a1/results?version=3");
        var (hidden, _) = await Access(Detail, HttpStatusCode.OK, """{"mode":"owner"}""");
        await Assert.That(hidden).IsEqualTo(PageResultsAccess.Hidden);
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound, PageResultsAccess.Hidden)]
    [Arguments(HttpStatusCode.Forbidden, PageResultsAccess.Hidden)]
    [Arguments(HttpStatusCode.ServiceUnavailable, PageResultsAccess.Unknown)]
    [Arguments(HttpStatusCode.Unauthorized, PageResultsAccess.Unknown)]
    [Arguments(HttpStatusCode.TooManyRequests, PageResultsAccess.Unknown)]
    public async Task Results_statuses_map_to_access(HttpStatusCode status, PageResultsAccess expected) {
        var (access, _) = await Access(Detail, status, "{}");
        await Assert.That(access).IsEqualTo(expected);
    }

    [Test]
    public async Task A_transport_failure_leaves_access_unknown() {
        var (access, _) = await Access(Detail, HttpStatusCode.OK, "{}", new HttpRequestException("down"));
        await Assert.That(access).IsEqualTo(PageResultsAccess.Unknown);
    }
}
