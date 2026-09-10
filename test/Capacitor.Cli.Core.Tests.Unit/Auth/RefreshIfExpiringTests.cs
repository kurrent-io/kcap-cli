using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Behaviour of <see cref="TokenStore.RefreshIfExpiringAsync"/> the daemon relies on. Most cases
/// are the no-op paths that must NOT touch the network — proactive refresh is a no-op when no
/// tokens are stored, for the None provider, and while the token is still comfortably valid
/// (refresh only inside the expiry window). The remaining case pins the classification the daemon
/// loop keys its hard backoff on: a WorkOS refresh the endpoint refuses reads as
/// <see cref="ProactiveRefreshOutcome.Rejected"/>, not <see cref="ProactiveRefreshOutcome.Failed"/>,
/// so the daemon stops re-sending a dead token. It reaches WorkOS through a local stub, never the
/// real host.
/// </summary>
public class RefreshIfExpiringTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Test]
    public async Task NotDue_when_no_tokens_stored() {
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }

    [Test]
    public async Task NotDue_and_leaves_token_untouched_when_comfortably_valid() {
        var original = new StoredTokens {
            AccessToken    = "at",
            RefreshToken   = "rt",
            ClientId       = "cid",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1), // well outside the 5-minute window
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS
        };
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", original);

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);

        // Untouched — no refresh attempted, so the persisted access token is unchanged.
        var after = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("default");
        await Assert.That(after!.AccessToken).IsEqualTo("at");
    }

    [Test]
    public async Task NotDue_for_none_provider_even_inside_window() {
        // A None-auth server stores no tokens; a stray Provider=None file must still be a no-op.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", new StoredTokens {
            AccessToken    = "at",
            ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(1), // inside the window
            GitHubUsername = "alice",
            Provider       = AuthProvider.None
        });

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }

    [Test]
    public async Task Rejected_when_workos_refuses_the_refresh_token() {
        // A refused refresh (400 invalid_grant) is terminal — the token is spent, only `kcap login`
        // repairs it — so the tick reports Rejected, distinct from a Failed transport blip.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"invalid_grant"}"""));

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", new StoredTokens {
            AccessToken    = "at",
            RefreshToken   = "rt",
            ClientId       = "cid",
            ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(1), // inside the window
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS
        });

        var outcome = await AuthFixtures.NewTokenStore(Config.Root, new StubHost(server.Urls[0]))
            .RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window);

        await Assert.That(outcome).IsEqualTo(ProactiveRefreshOutcome.Rejected);
    }
}
