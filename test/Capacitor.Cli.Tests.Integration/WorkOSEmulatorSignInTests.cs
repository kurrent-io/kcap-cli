using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// <c>kcap login</c>, a refresh and a forgotten session against the real WorkOS emulator, which the
/// CLI reaches only through the base a loopback server advertises at <c>/auth/config</c>.
///
/// <para>Linux containers only: on CI it runs on the Linux leg alone, and anywhere no container
/// engine answers it is skipped.</para>
/// </summary>
public class WorkOSEmulatorSignInTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Profile = "default";

    [Test]
    [Timeout(300_000)]
    public async Task Login_refresh_and_restart_against_the_emulator(CancellationToken ct) {
        Skip.When(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && !OperatingSystem.IsLinux(),
            "this CI runner cannot run Linux containers");

        var unavailable = await WorkOSEmulator.UnavailableReasonAsync();
        Skip.When(unavailable is not null, unavailable ?? "");

        await using var emulator = await WorkOSEmulator.StartAsync(ct);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                  $$"""
                  {"provider":"workos","client_id":"{{WorkOSEmulator.ClientId}}","authkit_domain":"",
                   "organization_id":"{{WorkOSEmulator.OrganizationId}}","api_base_url":"{{emulator.BaseUrl}}"}
                  """));

        var serverUrl = server.Urls[0];

        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile> { [Profile] = new() { ServerUrl = serverUrl } }
        }, ct);

        using var network  = new SocketsHttpHandler();
        using var launcher = new AuthorizeRedirectLauncher();
        var       progress = new RecordingAuthProgress();
        var       facade   = AuthFixtures.NewFacade(Config.Root, progress, network, browser: launcher);

        var result = await facade.LoginAsync(serverUrl, forceDevice: false, Profile, ct);
        await launcher.Callback;

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).CredentialSaved).IsTrue();
        await Assert.That(progress.Errors).IsEmpty();

        var store    = AuthFixtures.NewTokenStore(Config.Root, network);
        var signedIn = (await store.LoadAsync(Profile))!;

        await Assert.That(JwtClaims.TryGetString(signedIn.AccessToken, "sub")).IsEqualTo(WorkOSEmulator.OwnerId);
        await Assert.That(JwtClaims.TryGetString(signedIn.AccessToken, "org_id")).IsEqualTo(WorkOSEmulator.OrganizationId);
        await Assert.That(signedIn.GitHubUsername).IsEqualTo("Olive Owner");
        await Assert.That(signedIn.WorkOSApiBase).IsEqualTo(emulator.BaseUrl);

        var refreshed = await store.ForceRefreshAsync(Profile, signedIn.AccessToken, ct: ct);

        await Assert.That(refreshed).IsNotNull();
        await Assert.That(refreshed!.RefreshToken).IsNotEqualTo(signedIn.RefreshToken);
        await Assert.That((await store.LoadAsync(Profile))!.RefreshToken).IsEqualTo(refreshed.RefreshToken);

        await emulator.RestartAsync(ct);

        // Past the access token's hour, so the refresh is due and the stored token no longer serves.
        var later      = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(2));
        var laterStore = AuthFixtures.NewTokenStore(Config.Root, network, time: later);

        await Assert.That(await laterStore.RefreshIfExpiringAsync(Profile, TimeSpan.FromMinutes(5)))
            .IsEqualTo(ProactiveRefreshOutcome.Rejected);

        var resolution = await laterStore.GetValidTokensForServerAsync(Profile, serverUrl, ct);
        var notice     = AuthRejectionNotice.Render(
            AuthRejectionNotice.FromAuthStatus(resolution.Status), null, serverUrl, later);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.Expired);
        await Assert.That(notice).Contains("Run 'kcap login'");
    }
}
