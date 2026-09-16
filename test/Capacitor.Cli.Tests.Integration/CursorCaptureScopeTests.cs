using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Cursor's live hook applies the profile's capture scope. It reads <c>workspace_roots[0]</c> where
/// every other harness reads <c>cwd</c>, which is the whole reason it needs its own coverage: a gate
/// written against <c>cwd</c> reads null here and admits or drops everything.
/// </summary>
public class CursorCaptureScopeTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    async Task<int> RunSessionStartAsync(Profile profile, string workspaceRoot) {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = profile with { ServerUrl = _server.Url } },
        });

        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/api/*").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        var body =
            $$"""
            {
              "hook_event_name": "sessionStart",
              "session_id":      "cursorscopetestsession",
              "workspace_roots": ["{{workspaceRoot.Replace("\\", "\\\\")}}"]
            }
            """;

        using var tmp    = new TempDir();
        using var client = new HttpClient();
        var       spool  = new HookSpool(tmp.CreateDir("spool").Path);

        return await new CursorHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root),
            new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), HostedAgent.Terminal,
            new FixedCapacitorHttpClient(),
            TestWatchers.For(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()),
            router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory))
            .HandleCore(client, new StringReader(body), spool);
    }

    int SessionStartPosts() =>
        _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost()).Count;

    [Test]
    public async Task A_workspace_under_an_ignored_path_posts_nothing() {
        using var tmp     = new TempDir();
        var       ignored = tmp.CreateDir("ignored");

        var exit = await RunSessionStartAsync(new Profile { ExcludedPaths = [ignored] }, ignored);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(SessionStartPosts()).IsEqualTo(0);
    }

    [Test]
    public async Task A_workspace_outside_the_allow_list_posts_nothing() {
        using var tmp     = new TempDir();
        var       allowed = tmp.CreateDir("allowed");
        var       other   = tmp.CreateDir("other");

        var exit = await RunSessionStartAsync(new Profile { AllowedPaths = [allowed] }, other);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(SessionStartPosts()).IsEqualTo(0);
    }

    [Test]
    public async Task A_workspace_inside_the_allow_list_is_captured() {
        using var tmp     = new TempDir();
        var       allowed = tmp.CreateDir("allowed");

        var exit = await RunSessionStartAsync(new Profile { AllowedPaths = [allowed] },
                                              allowed.CreateDir("project"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(SessionStartPosts()).IsEqualTo(1);
    }

    [Test]
    public async Task An_unscoped_profile_is_captured() {
        using var tmp = new TempDir();

        var exit = await RunSessionStartAsync(new Profile(), tmp.CreateDir("anywhere"));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(SessionStartPosts()).IsEqualTo(1);
    }
}
