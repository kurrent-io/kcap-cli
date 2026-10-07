using System.Net;
using System.Text;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Core.Tests.Unit.Plans;

/// The channel's totalization: which statuses mean signed out, unavailable or unreachable, and that
/// a good body parses into the DTO with its chain query on the URL.
public class PlanArtifactsClientTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Body = """
        {"primary":null,"artifacts":[{"artifact_id":"a1","kind":"design","title":"Design","source":"declared","session_id":"0123456789abcdef0123456789abcdef","path":"docs/x-design.md","content":"# Design","content_state":"ok","is_complete":true,"is_confirmed":true,"content_hash":"abc","version":1,"discovered_at":"2026-10-07T10:00:00Z","confidence":"high","reason":"declared","is_primary":true}],"diagnostics":[]}
        """;

    sealed class Handler(HttpStatusCode status, string body = "{}") : HttpMessageHandler {
        public Uri? Requested;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    static (PlanArtifactsClient Client, Handler Handler) Build(HttpStatusCode status, string body = "{}") {
        var handler = new Handler(status, body);
        return (new PlanArtifactsClient(new HttpClient(handler), "https://server.test/"), handler);
    }

    [Test]
    public async Task A_good_body_reads_ready_with_the_chain_query() {
        var (client, handler) = Build(HttpStatusCode.OK, Body);
        var read = await client.ReadAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Body!.Artifacts.Single().Path).IsEqualTo("docs/x-design.md");
        await Assert.That(handler.Requested!.ToString()).IsEqualTo($"https://server.test/api/sessions/{Session}/plan-artifacts?chain=true");
    }

    [Test]
    [Arguments(HttpStatusCode.Unauthorized, SessionPlansReadKind.SignedOut)]
    [Arguments(HttpStatusCode.Forbidden, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.NotFound, SessionPlansReadKind.Unavailable)]
    [Arguments(HttpStatusCode.BadGateway, SessionPlansReadKind.Unreachable)]
    public async Task Statuses_totalize(HttpStatusCode status, SessionPlansReadKind expected) {
        var (client, _) = Build(status);
        var read = await client.ReadAsync(Session, CancellationToken.None);
        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Body).IsNull();
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
