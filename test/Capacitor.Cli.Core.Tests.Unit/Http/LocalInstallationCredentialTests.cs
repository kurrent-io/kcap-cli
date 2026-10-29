using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// A local installation's server rejects any request carrying a bearer, so none is sent whatever the
/// token store holds — a machine converted from cloud keeps its tokens. The stub announces a provider
/// that needs one and holds a token bound to it, so a bearer or a refresh can only be withheld by the
/// classification.
/// </summary>
public class LocalInstallationCredentialTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    string Url => _server.Urls[0];

    public void Dispose() => _server.Stop();

    ServiceProvider Container(ProfileContext profiles) =>
        new ServiceCollection()
            .AddSingleton(Config.Root)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(profiles.Resolution.ServerUrl, Config.Root, profiles))
            .AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None)
            .BuildServiceProvider();

    async Task<ProfileContext> LeftBehindAsync(LocalEndpoints registry, DateTimeOffset expiresAt) {
        var profiles = Resolutions.AtInstallation(Url, Config.Root, registry);

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken    = "cloud-token",
            RefreshToken   = "cloud-refresh",
            ExpiresAt      = expiresAt,
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = Url,
        });

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{AuthProvider.GitHubApp}}"}"""));
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        return profiles;
    }

    [Test]
    public async Task A_request_to_the_installation_carries_no_bearer_and_asks_for_no_provider() {
        var profiles = await LeftBehindAsync(Resolutions.InstalledAt(Url), DateTimeOffset.UtcNow.AddHours(1));
        await Assert.That(profiles.Resolution.Endpoint).IsTypeOf<LocalEndpointClass.Local>();

        using var sp       = Container(profiles);
        using var client   = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        using var response = await client.GetAsync($"{Url}/ping");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).IsEquivalentTo(["/ping"]);
        await Assert.That(_server.LogEntries.Single().RequestMessage.Headers!.ContainsKey("Authorization")).IsFalse();
    }

    /// <summary>The outage case: discovery's answer is a cached provider that needs a bearer, and an
    /// expired token would be refreshed before it was sent.</summary>
    [Test]
    public async Task An_expired_token_left_behind_is_not_refreshed_while_the_provider_cache_says_otherwise() {
        var profiles = await LeftBehindAsync(Resolutions.InstalledAt(Url), DateTimeOffset.UtcNow.AddMinutes(-5));
        AuthProviderCache.Set(Url, AuthProvider.GitHubApp, Config.Root, TimeProvider.System);

        using var sp = Container(profiles);
        var state = await sp.GetRequiredService<ICredentialSource>().ResolveAsync(CancellationToken.None);

        await Assert.That(state.Bearer).IsNull();
        await Assert.That(state.Status).IsEqualTo(AuthStatus.NoAuthRequired);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync(profiles.Name))!.AccessToken).IsEqualTo("cloud-token");
    }

    /// <summary>A purged installation's port, or a loopback port while the registry cannot be read, may
    /// be held by anything: it is never sent a bearer either.</summary>
    [Test]
    public async Task A_retired_or_undetermined_loopback_origin_gets_no_bearer() {
        foreach (var registry in new[] { Resolutions.RetiredAt(Url), Resolutions.UnreadableRegistry }) {
            var profiles = await LeftBehindAsync(registry, DateTimeOffset.UtcNow.AddHours(1));

            using var sp = Container(profiles);
            var state = await sp.GetRequiredService<ICredentialSource>().ResolveAsync(CancellationToken.None);

            await Assert.That(state.Bearer).IsNull();
        }

        await Assert.That(_server.LogEntries).IsEmpty();
    }

    /// <summary>The same token is sent when the registry does not name this origin, which is what makes
    /// the cases above prove the classification rather than a broken fixture.</summary>
    [Test]
    public async Task The_same_token_is_sent_to_an_ordinary_server() {
        var profiles = await LeftBehindAsync(LocalEndpoints.None, DateTimeOffset.UtcNow.AddHours(1));

        using var sp = Container(profiles);
        var state = await sp.GetRequiredService<ICredentialSource>().ResolveAsync(CancellationToken.None);

        await Assert.That(state.Bearer).IsEqualTo("cloud-token");
    }
}
