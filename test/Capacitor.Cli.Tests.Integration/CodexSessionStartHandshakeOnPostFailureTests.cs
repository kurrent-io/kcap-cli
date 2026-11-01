using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Codex blocks on its SessionStart hook's stdout and its parser rejects empty output, so a permanent
/// lifecycle-POST rejection must still produce the handshake: the recording outcome cannot decide
/// whether the host can proceed. The non-zero exit still reports that the session was not recorded.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class CodexSessionStartHandshakeOnPostFailureTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server         = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task A_rejected_lifecycle_post_still_satisfies_the_blocking_stdout_handshake() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        // A permanent rejection: PostOrSpoolAsync returns Failed for a genuine non-2xx; transport and
        // auth failures spool instead.
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        // No transcript_path → short-circuits before the watcher spawn.
        const string payload =
            """
            {
              "hook_event_name": "SessionStart",
              "session_id":      "handshake-on-failure",
              "cwd":             "/tmp",
              "model":           "gpt-5"
            }
            """;

        var stdout = new StringWriter();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .Handle(new StringReader(payload), new CodexHookEnvironment(stdout, TextWriter.Null, Skip: false, HostedAppServer: false));

        await Assert.That(exit).IsEqualTo(1);

        await Assert.That(stdout.ToString()).IsNotEmpty();
        var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        // And the POST really was attempted and really was rejected, so the assertions above are
        // about the Failed path and not some earlier short-circuit.
        var requests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);
    }
}
