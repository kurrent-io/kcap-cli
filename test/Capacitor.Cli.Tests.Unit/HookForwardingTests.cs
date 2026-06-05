using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class PostWithRetryTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SucceedsOnFirstAttempt() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var client   = new HttpClient();
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostWithRetryAsync($"{_server.Url}/hooks/session-start", content);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task RetriesOnConnectionFailureThenSucceeds() {
        var tempServer = WireMockServer.Start();
        var port       = tempServer.Port;
        var url        = $"http://localhost:{port}/hooks/test";
        tempServer.Stop();

        WireMockServer? restartedServer = null;

        _ = Task.Run(async () => {
                await Task.Delay(600);
                restartedServer = WireMockServer.Start(port);

                restartedServer.Given(Request.Create().WithPath("/hooks/test").UsingPost())
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody("recovered"));
            }
        );

        try {
            using var client   = new HttpClient();
            using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
            var       response = await client.PostWithRetryAsync(url, content, timeout: TimeSpan.FromSeconds(15));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        } finally {
            restartedServer?.Stop();
        }
    }
}

public class InlineDrainTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task PostsCorrectBatch() {
        const string sessionId = "test-session-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var dir            = Path.Combine(Path.GetTempPath(), $"kcap_test_{Guid.NewGuid():N}");
        var transcriptPath = Path.Combine(dir, "transcript.jsonl");
        Directory.CreateDirectory(dir);

        try {
            await File.WriteAllTextAsync(
                transcriptPath,
                """{"type":"user","message":{"content":"hello"}}"""        + "\n" +
                """{"type":"assistant","message":{"content":"hi back"}}""" + "\n"
            );

            await WatcherManager.InlineDrainAsync(_server.Url!, sessionId, transcriptPath, null);

            var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

            await Assert.That(requests.Count).IsEqualTo(1);

            var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
            await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
            await Assert.That(root.GetProperty("lines").GetArrayLength()).IsEqualTo(2);
            await Assert.That(root.GetProperty("line_numbers")[0].GetInt32()).IsEqualTo(0);
            await Assert.That(root.GetProperty("line_numbers")[1].GetInt32()).IsEqualTo(1);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PostsCorrectBatch_with_codex_vendor_when_specified() {
        const string sessionId = "test-session-codex-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var dir            = Path.Combine(Path.GetTempPath(), $"kcap_test_{Guid.NewGuid():N}");
        var transcriptPath = Path.Combine(dir, "rollout.jsonl");
        Directory.CreateDirectory(dir);

        try {
            await File.WriteAllTextAsync(
                transcriptPath,
                """{"timestamp":"2026-05-07T15:50:21.989Z","type":"session_meta","payload":{}}""" + "\n"
            );

            await WatcherManager.InlineDrainAsync(_server.Url!, sessionId, transcriptPath, agentId: null, vendor: "codex");

            var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

            await Assert.That(requests.Count).IsEqualTo(1);

            var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
            await Assert.That(root.GetProperty("vendor").GetString()).IsEqualTo("codex");
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class SessionStartAdditionalContextTests {
    // Repopulated in Task 5 with aggregator tests.
}
