using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// <c>kcap hook --grok</c> against a stub server. The payloads are the shapes a live Grok 1.0.52 run
/// handed its hooks: camelCase keys, snake_case event values, and a transcript path on every event but
/// session start.
/// </summary>
public class GrokHookCommandTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }

    readonly WireMockServer     _server  = WireMockServer.Start();
    readonly FakeWatcherSpawner _spawner = new();

    const string DashedSessionId = "01a136cc-c631-7762-9e1a-e6b2dbe41f55";
    const string SessionId       = "01a136ccc63177629e1ae6b2dbe41f55";

    public void Dispose() => _server.Stop();

    async Task<int> RunAsync(JsonObject payload) {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        });

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();

        return await new GrokHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home,
                TestHarnesses.Under(Home), http, TestWatchers.For(Config.Root, profiles, http, _spawner), new GitProviderRouter())
            .Handle(new StringReader(payload.ToJsonString()));
    }

    static JsonObject Payload(string hookEventName, string cwd, string? transcriptPath = null) {
        var payload = new JsonObject {
            ["hookEventName"]   = hookEventName,
            ["sessionId"]       = DashedSessionId,
            ["cwd"]             = cwd,
            ["workspaceRoot"]   = cwd,
            ["timestamp"]       = "2026-10-13T19:45:52.612399+00:00",
            ["source"]          = "new",
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = DashedSessionId,
        };
        if (transcriptPath is not null) payload["transcriptPath"] = transcriptPath;

        return payload;
    }

    [Test, NotInParallel]
    public async Task Session_start_posts_the_session_and_watches_its_derived_updates_file() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/grok").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        var cwd = "/repo/demo";

        var exit = await RunAsync(Payload("session_start", cwd));

        await Assert.That(exit).IsEqualTo(0);

        var post = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/grok").UsingPost()).Single();
        using var body = JsonDocument.Parse(post.RequestMessage.Body!);
        await Assert.That(body.RootElement.GetProperty("session_id").GetString()).IsEqualTo(SessionId);
        await Assert.That(body.RootElement.GetProperty("cwd").GetString()).IsEqualTo(cwd);
        await Assert.That(body.RootElement.GetProperty("workspace_root").GetString()).IsEqualTo(cwd);
        await Assert.That(body.RootElement.GetProperty("started_at").GetString()).StartsWith("2026-10-13T19:45:52");

        var spawned = _spawner.Spawned.Single();
        await Assert.That(spawned.Vendor).IsEqualTo("grok");
        await Assert.That(spawned.TranscriptPath).IsEqualTo(
            Path.Combine(Home.Path, ".grok", "sessions", "%2Frepo%2Fdemo", DashedSessionId, "updates.jsonl"));
    }

    /// <summary>A permanent rejection must not leave a watcher shipping lines for a session the server
    /// refused — and must still not fail the Grok hook.</summary>
    [Test, NotInParallel]
    public async Task A_rejected_session_start_starts_no_watcher_and_still_exits_zero() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/grok").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        var exit = await RunAsync(Payload("session_start", "/repo/demo"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_spawner.Spawned).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task Stop_restarts_a_missing_watcher_on_the_named_transcript_without_posting() {
        var transcript = Path.Combine(Home.Path, "elsewhere", "updates.jsonl");

        var exit = await RunAsync(Payload("stop", "/repo/demo", transcript));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(_spawner.Spawned.Single().TranscriptPath).IsEqualTo(transcript);
    }

    [Test, NotInParallel]
    public async Task Events_it_does_not_handle_do_nothing() {
        var exit = await RunAsync(Payload("user_prompt_submit", "/repo/demo"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(_spawner.Spawned).IsEmpty();
    }
}
