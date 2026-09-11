using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap update --check</c> refreshes the server version before capping the install at it: the cached
/// value changes only on an authenticated response, so a server upgraded since the last one would pin the
/// update to its old version. Here npm latest is 999.0.0, the cache holds 997.0.0 and the live server
/// answers 998.0.0.
/// </summary>
// Bare, not keyed: the command writes to Console, and KCAP_URL is read by every resolution in the assembly.
[NotInParallel]
public class UpdateCommandServerRefreshTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    ServiceProvider? _sp;

    public void Dispose() {
        _sp?.Dispose();
        _server.Stop();
    }

    void StubRegistryAndServer() {
        _server.Given(Request.Create().WithPath("/@kurrent/kcap/latest").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"version":"999.0.0"}"""));
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"github_app"}"""));
        _server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "998.0.0"));

        ServerVersionStore.Set(_server.Urls[0], "997.0.0", Config.Root);
    }

    ProfileContext Profiles(string profileName) =>
        Resolutions.Of(new Profile { ServerUrl = _server.Urls[0] }, profileName, _server.Urls[0]);

    async Task<ProfileContext> SeedValidTokenAsync(string profileName) {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-" + profileName,
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        return Profiles(profileName);
    }

    async Task<JsonObject> CheckAsync(ProfileContext profiles) {
        var services = new ServiceCollection();

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(profiles.Resolution.ServerUrl ?? "", Config.Root, profiles));
        services.AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None);
        _sp = services.BuildServiceProvider();

        using var registry = new HttpClient { BaseAddress = new Uri(_server.Urls[0] + "/") };

        var command = new UpdateCommand(
            Config.Root, profiles, new NpmRegistryClient(registry),
            _sp.GetRequiredService<CapacitorServer>(), _sp.GetRequiredService<ICapacitorHttpClient>(), appBundled: false);

        using var output = ConsoleOutput.StartCapture();
        await command.HandleAsync(["--check"]);

        return JsonNode.Parse(output.GetCapturedOutput().Trim())!.AsObject();
    }

    [Test]
    public async Task The_live_server_version_replaces_the_cached_one_before_capping() {
        StubRegistryAndServer();

        var json = await CheckAsync(await SeedValidTokenAsync("update-refresh"));

        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("998.0.0");
        await Assert.That(ServerVersionStore.Get(_server.Urls[0], Config.Root)).IsEqualTo("998.0.0");
    }

    /// <summary>Without a credential no probe goes out, and the cached version still caps the install.</summary>
    [Test]
    public async Task Without_a_credential_the_cached_version_still_caps() {
        StubRegistryAndServer();

        var json = await CheckAsync(Profiles("update-no-token"));

        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("997.0.0");
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == WhoamiCommand.ProbePath)).IsFalse();
    }
}
