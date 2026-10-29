using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// whoami asks discovery first and then probes with the stored token. Against a local installation
/// discovery can answer with a stored token's provider while the server is down, and the probe would
/// then send that token; the installation is decided before either.
/// </summary>
[NotInParallel]
public class WhoamiLocalInstallationTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    string Url => _server.Urls[0];

    public void Dispose() => _server.Stop();

    async Task<(int Exit, string Out)> RunAsync(ProfileContext profiles) {
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync(profiles.Name, new StoredTokens {
            AccessToken    = "cloud-token",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = Url,
        });
        AuthProviderCache.Set(Url, AuthProvider.GitHubApp, Config.Root, TimeProvider.System);

        using var capture = ConsoleOutput.StartCapture();

        var exit = await new WhoamiCommand(
            Config.Root, profiles, tokens, new FixedCapacitorHttpClient(),
            new AuthProviderDiscovery(new PlainHttpClientFactory(), TimeProvider.System), TimeProvider.System).HandleAsync();

        return (exit, capture.GetCapturedOutput());
    }

    [Test]
    public async Task The_installation_reports_no_provider_and_sends_nothing() {
        var (exit, output) = await RunAsync(Resolutions.AtInstallation(Url, Config.Root, Resolutions.InstalledAt(Url)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("None (local installation)");
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_retired_installation_is_an_error_and_sends_nothing() {
        var (exit, _) = await RunAsync(Resolutions.AtInstallation(Url, Config.Root, Resolutions.RetiredAt(Url)));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_server.LogEntries).IsEmpty();
    }
}
