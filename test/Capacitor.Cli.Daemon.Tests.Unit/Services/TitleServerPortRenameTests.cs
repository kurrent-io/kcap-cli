using System.Text.Json.Nodes;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[NotInParallel(nameof(TitleServerPortRenameTests))]
public class TitleServerPortRenameTests : IDisposable {
    const string Guid32 = "0123456789abcdef0123456789abcdef";
    const string GuidDashed = "01234567-89ab-cdef-0123-456789abcdef";

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    TitleServerPort Port() => new(new FixedCapacitorHttpClient(), _server.Url!);

    void Reply(int status, string body = "") =>
        _server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Test]
    public async Task Posts_rename_true_with_canonical_session_id() {
        Reply(200, """{"applied":true,"title":"New"}""");
        await Port().RenameAsync(GuidDashed, "New", CancellationToken.None);

        var body = JsonNode.Parse(_server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That((string?)body["session_id"]).IsEqualTo(Guid32);
        await Assert.That((string?)body["title"]).IsEqualTo("New");
        await Assert.That((bool)body["rename"]!).IsTrue();
    }

    [Test]
    public async Task Applied_reply_returns_shown_title() {
        Reply(200, """{"applied":true,"title":"Shown"}""");
        var outcome = await Port().RenameAsync(Guid32, "Asked", CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(new TitleRenameOutcome(true, "Shown", null));
    }

    [Test]
    public async Task Applied_false_is_refused() {
        Reply(200, """{"applied":false,"title":"Kept"}""");
        var outcome = await Port().RenameAsync(Guid32, "Asked", CancellationToken.None);
        await Assert.That(outcome.Applied).IsFalse();
        await Assert.That(outcome.Reason).IsEqualTo(SessionRenameReasons.Refused);
    }

    [Test]
    public async Task Forbidden_is_not_owned() {
        Reply(403);
        var outcome = await Port().RenameAsync(Guid32, "x", CancellationToken.None);
        await Assert.That(outcome.Reason).IsEqualTo(SessionRenameReasons.NotOwned);
    }

    [Test]
    public async Task Not_found_is_not_found() {
        Reply(404);
        var outcome = await Port().RenameAsync(Guid32, "x", CancellationToken.None);
        await Assert.That(outcome.Reason).IsEqualTo(SessionRenameReasons.NotFound);
    }

    [Test]
    public async Task Server_error_is_server_error() {
        Reply(500);
        var outcome = await Port().RenameAsync(Guid32, "x", CancellationToken.None);
        await Assert.That(outcome.Reason).IsEqualTo(SessionRenameReasons.ServerError);
    }
}
