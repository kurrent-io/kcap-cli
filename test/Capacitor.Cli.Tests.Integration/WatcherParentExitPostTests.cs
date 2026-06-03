using System.Text.Json;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Validates the watcher's parent-exit session-end POST: when the watcher detects
/// its parent coding-agent process has died without firing session-end, it takes
/// over the role and POSTs to <c>/hooks/session-end/{vendor}</c> with
/// <c>reason: parent_exited</c>. The server's idempotent HandleSessionEnd writes
/// SessionEnded and tells us whether to spawn the what's-done generator.
/// </summary>
public class WatcherParentExitPostTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task ParentExit_PostsSessionEnd_WithExpectedPayload() {
        // Auth discovery: "None" means no Bearer token needed for subsequent calls.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/claude").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"generate_whats_done":false}""")
            );

        var sessionId = $"test-{Guid.NewGuid():N}";

        await WatchCommand.PostSessionEndOnParentExitAsync(
            baseUrl:        _server.Url!,
            sessionId:      sessionId,
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "claude",
            repository:     null
        );

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo("/tmp/fake.jsonl");
        await Assert.That(body.GetProperty("cwd").GetString()).IsEqualTo("/repo");
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("session_end");
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("parent_exited");
    }

    [Test]
    public async Task ParentExit_IncludesRepository_WhenProvided() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/claude").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        var sessionId = $"test-{Guid.NewGuid():N}";

        await WatchCommand.PostSessionEndOnParentExitAsync(
            baseUrl:        _server.Url!,
            sessionId:      sessionId,
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "claude",
            repository: new() {
                Owner    = "kurrent-io",
                RepoName = "kapacitor",
                Branch   = "main",
            }
        );

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        var repo = body.GetProperty("repository");
        await Assert.That(repo.GetProperty("owner").GetString()).IsEqualTo("kurrent-io");
        await Assert.That(repo.GetProperty("repo_name").GetString()).IsEqualTo("kapacitor");
        await Assert.That(repo.GetProperty("branch").GetString()).IsEqualTo("main");
    }

    [Test]
    public async Task ParentExit_SkipsPost_ForUnknownVendor() {
        // Vendor is interpolated into the URL path. Validate against a known whitelist
        // to defend against malformed --vendor values producing path traversal or
        // hitting unintended endpoints.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        // Catch-all for any session-end variant so we can assert nothing was posted.
        _server.Given(Request.Create().WithPath("/hooks/session-end/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await WatchCommand.PostSessionEndOnParentExitAsync(
            baseUrl:        _server.Url!,
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "../admin",
            repository:     null
        );

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/*").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParentExit_PostsToVendorSpecificRoute_ForCodex() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await WatchCommand.PostSessionEndOnParentExitAsync(
            baseUrl:        _server.Url!,
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "codex",
            repository:     null
        );

        var codexHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        var claudeHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(codexHits.Count).IsEqualTo(1);
        await Assert.That(claudeHits.Count).IsEqualTo(0);
    }
}
