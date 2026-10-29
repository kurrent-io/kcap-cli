using Capacitor.Cli.Core.Plans;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Plans;

/// The session-plans route, its id containment, and the status each outcome totalizes to.
public class SessionPlansClientTests {
    const string Dashed   = "01234567-89ab-cdef-0123-456789abcdef";
    const string Dashless = "0123456789abcdef0123456789abcdef";

    sealed class ThrowingHandler(Exception exception) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    sealed class CancellingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    static WireMockServer Serve(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/sessions/{Dashless}/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
        return server;
    }

    [Test]
    public async Task The_route_strips_dashes_and_parses_plans_with_their_documents_and_tasks() {
        using var server = Serve(200, """
            [{"plan_id":"p1","key_kind":"document","is_current":true,
              "documents":[{"document_key":"k1","kind":"spec","path":"docs/specs/plan-widget.md","workspace_root":"/repo","has_content":true}],
              "tasks":[{"task_id":"t1","ordinal":1,"title":"Read the route","status":"in_progress","note":"half way","source":"mcp","status_partial":false}],
              "sessions":["s1"],
              "progress":{"completed":0,"total":1,"total_known":true},
              "is_complete":true,"withheld_contributions":0}]
            """);
        using var http = new HttpClient();

        var read = await new SessionPlansClient(http, server.Urls[0] + "/").ReadAsync(Dashed, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        var plan = read.Plans.Single();
        await Assert.That(plan.PlanId).IsEqualTo("p1");
        await Assert.That(plan.IsCurrent).IsTrue();
        await Assert.That(plan.Documents.Single().Kind).IsEqualTo("spec");
        await Assert.That(plan.Documents.Single().Path).IsEqualTo("docs/specs/plan-widget.md");
        await Assert.That(plan.Tasks.Single().Status).IsEqualTo("in_progress");
        await Assert.That(plan.Tasks.Single().Note).IsEqualTo("half way");
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo($"/api/sessions/{Dashless}/plans");
    }

    [Test]
    public async Task A_session_with_no_plans_is_ready_and_empty() {
        using var server = Serve(200, "[]");
        using var http = new HttpClient();

        var read = await new SessionPlansClient(http, server.Urls[0]).ReadAsync(Dashed, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(read.Plans.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(401, SessionPlansReadKind.SignedOut)]
    [Arguments(403, SessionPlansReadKind.Unavailable)]
    [Arguments(404, SessionPlansReadKind.Unavailable)]
    [Arguments(500, SessionPlansReadKind.Unreachable)]
    [Arguments(503, SessionPlansReadKind.Unreachable)]
    public async Task A_refusal_totalizes_by_status(int status, SessionPlansReadKind expected) {
        using var server = Serve(status, """{"error":"nope"}""");
        using var http = new HttpClient();

        var read = await new SessionPlansClient(http, server.Urls[0]).ReadAsync(Dashed, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Plans.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_success_that_does_not_parse_is_unreachable_rather_than_an_empty_plan_list() {
        using var server = Serve(200, "<html>proxy</html>");
        using var http = new HttpClient();

        var read = await new SessionPlansClient(http, server.Urls[0]).ReadAsync(Dashed, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unreachable);
    }

    [Test]
    public async Task A_transport_failure_is_unreachable() {
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        var read = await new SessionPlansClient(http, "http://localhost:1").ReadAsync(Dashed, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unreachable);
    }

    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task An_id_that_would_escape_or_empty_the_route_is_refused_before_any_request(string id) {
        using var http = new HttpClient(new ThrowingHandler(new InvalidOperationException("a request was sent")));

        var read = await new SessionPlansClient(http, "http://localhost:1").ReadAsync(id, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.Unavailable);
    }

    [Test]
    public async Task The_callers_own_cancellation_propagates_instead_of_reading_as_an_outage() {
        using var http = new HttpClient(new CancellingHandler());
        using var cts = new CancellationTokenSource();
        var pending = new SessionPlansClient(http, "http://localhost:1").ReadAsync(Dashed, cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
