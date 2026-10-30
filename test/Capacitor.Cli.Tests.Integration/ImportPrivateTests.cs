using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class ImportPrivateTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    [Test]
    public async Task SetVisibilityNoneForAll_calls_PUT_for_each_session_id() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        await ImportCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"], TimeProvider.System, new ImportCommand.ImportDisplay { Terminal = TestImportConsole.Over() });

        var requests = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .OrderBy(e => e.RequestMessage.Path)
            .ToArray();

        await Assert.That(requests.Select(r => r.RequestMessage.Path).ToArray()).IsEquivalentTo(
        [
            "/api/sessions/sess1/visibility",
            "/api/sessions/sess2/visibility",
            "/api/sessions/sess3/visibility"
        ]
        );

        foreach (var r in requests) {
            await Assert.That(r.RequestMessage.Body).IsEqualTo("""{"visibility":"none"}""");
        }
    }

    [Test]
    public async Task SetVisibilityNoneForAll_continues_on_per_session_failure() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(400));
        _server.Given(Request.Create().WithPath("/api/sessions/sess*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        // Should not throw even though sess2 returns 400.
        await ImportCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"], TimeProvider.System, new ImportCommand.ImportDisplay { Terminal = TestImportConsole.Over() });

        var attempted = _server.LogEntries
            .Count(e => e.RequestMessage.Method == "PUT");

        await Assert.That(attempted).IsEqualTo(3);
    }

    /// <summary>The failure line goes to the run's stderr, under the phase heading above it.</summary>
    [Test]
    public async Task SetVisibilityForAll_reports_a_failure_on_the_margin_it_was_given() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(400));

        using var client = new HttpClient();
        var       stderr = new StringWriter();

        var lost = await ImportCommand.SetVisibilityForAll(
            client, _server.Url!, ["sess1"], "org", TimeProvider.System,
            new ImportCommand.ImportDisplay { Terminal = TestImportConsole.Over(stderr: stderr), Nested = true });

        await Assert.That(lost).IsEquivalentTo(new[] { "sess1" });
        await Assert.That(stderr.ToString())
                    .StartsWith("    ! visibility=org failed for sess1: HTTP 400");
    }
}

