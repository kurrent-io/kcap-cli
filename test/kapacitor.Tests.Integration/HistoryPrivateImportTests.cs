using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Integration;

public class HistoryPrivateImportTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kapacitor-private-test").FullName;

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task SetVisibilityNoneForAll_calls_PUT_for_each_session_id() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        await HistoryCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var calls = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Select(e => e.RequestMessage.Path)
            .OrderBy(p => p)
            .ToArray();

        await Assert.That(calls).IsEquivalentTo(new[] {
            "/api/sessions/sess1/visibility",
            "/api/sessions/sess2/visibility",
            "/api/sessions/sess3/visibility",
        });
    }

    [Test]
    public async Task SetVisibilityNoneForAll_continues_on_per_session_failure() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/api/sessions/sess*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        // Should not throw even though sess2 returns 500.
        await HistoryCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var attempted = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Count();

        await Assert.That(attempted).IsEqualTo(3);
    }
}
