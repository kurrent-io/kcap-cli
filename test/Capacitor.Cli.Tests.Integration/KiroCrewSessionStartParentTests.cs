using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A Kiro Crew sub-agent's agentSpawn names the session that spawned it, joined from Crew's own files,
/// so the server can nest it. The session id rides the payload: <c>KIRO_SESSION_ID</c> is unset here.
/// </summary>
public class KiroCrewSessionStartParentTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Chat   = "dashboard:chat-2-1790266418";
    const string Parent = "ebc247c2-22b7-4f08-bbcf-f7053162d361";
    const string Child  = "c9996655-c526-4524-86f9-4ebd14dba13a";

    KiroCrewPaths Crew => TestHarnesses.Under(Home).Of<KiroHarness>().Crew;

    void SeedSessionMap() {
        Directory.CreateDirectory(Crew.Root);
        File.WriteAllText(Crew.SessionMapJson, $"{{\"{Chat}\": {{\"sid\": \"{Parent}\", \"provider\": \"acp\"}}}}");

        var sessions = TestHarnesses.Under(Home).Of<KiroHarness>().Paths.SessionsDir;
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"{Parent}.json"), $"{{\"session_id\": \"{Parent}\", \"created_at\": \"2026-09-24T16:14:35.000000Z\"}}");
    }

    /// <summary>Spawned a few minutes after the parent session was created.</summary>
    static string SubagentState(bool withSessionId) =>
        $"{{\"id\": \"65eed35b\", \"parent_session\": \"{Chat}\", \"started\": 1790266768.153635, \"status\": \"running\""
      + (withSessionId ? $", \"session_id\": \"{Child}\"}}" : "}");

    void SeedSubagent(bool withSessionId) {
        var dir = Path.Combine(Crew.SubagentsDir, "65eed35b");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"), SubagentState(withSessionId));
    }

    async Task<JsonNode> SpawnAsync(string sessionId) {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        });

        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();
        var watchers = TestWatchers.For(Config.Root, profiles, http, new FakeWatcherSpawner(_ => Task.CompletedTask));

        await new KiroHookCommand(
                Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
                HostedAgent.Terminal, http, watchers, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory))
            .Handle(new StringReader($"{{\"hook_event_name\": \"agentSpawn\", \"cwd\": \"/tmp\", \"session_id\": \"{sessionId}\"}}"),
                ["hook", "--kiro", "--event", "agentSpawn"]);

        // The hook stops waiting on the POST at its ceiling and spools it; the request itself still lands.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost());
        while (requests.Count == 0 && DateTime.UtcNow < deadline) {
            await Task.Delay(100);
            requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost());
        }

        await Assert.That(requests.Count).IsEqualTo(1);

        return JsonNode.Parse(requests[0].RequestMessage.Body!)!;
    }

    [Test]
    public async Task A_crew_sub_agent_names_its_parent() {
        SeedSessionMap();
        SeedSubagent(withSessionId: true);

        var body = await SpawnAsync(Child);

        await Assert.That(body["parent_session_id"]?.GetValue<string>()).IsEqualTo(Parent);
    }

    /// <summary>A one-prompt sub-agent fires agentSpawn once, possibly before Crew records its session.</summary>
    [Test]
    public async Task A_sub_agent_Crew_records_moments_later_still_names_its_parent() {
        SeedSessionMap();
        SeedSubagent(withSessionId: false);

        var late = Task.Run(async () => {
            await Task.Delay(300);
            await File.WriteAllTextAsync(Path.Combine(Crew.SubagentsDir, "65eed35b", "state.json"), SubagentState(withSessionId: true));
        });

        var body = await SpawnAsync(Child);
        await late;

        await Assert.That(body["parent_session_id"]?.GetValue<string>()).IsEqualTo(Parent);
    }

    [Test]
    public async Task A_crew_chat_session_names_no_parent() {
        SeedSessionMap();
        SeedSubagent(withSessionId: true);

        var body = await SpawnAsync(Parent);

        await Assert.That(body["parent_session_id"]).IsNull();
    }

    [Test]
    public async Task A_plain_kiro_session_names_no_parent() {
        var body = await SpawnAsync(Child);

        await Assert.That(body["parent_session_id"]).IsNull();
    }
}
