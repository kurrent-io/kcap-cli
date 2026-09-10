using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Which server an UNBOUND token refreshes against. A bound token names its own minter and never
/// reaches this precedence; a pre-binding one has to be told, and getting it wrong reports a
/// perfectly good credential as expired.
/// </summary>
public class RefreshServerSelectionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileUrl = "https://profile.example";

    /// Records where the refresh was posted and answers a token so the lane completes.
    sealed class RefreshRecorder : HttpMessageHandler {
        public Uri? PostedTo { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) {
            PostedTo = r.RequestUri;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"access_token":"fresh","expires_in":3600}""",
                                            System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    async Task WriteProfile() =>
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "default",
            Profiles      = new() { ["default"] = new Profile { ServerUrl = ProfileUrl } }
        });

    static StoredTokens UnboundExpired() => new() {
        AccessToken    = "stale",
        RefreshToken   = "rt",
        ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(-10),
        GitHubUsername = "alice",
        Provider       = "GitHubApp"
    };

    async Task<Uri?> RefreshUnder(ProfileOverrides env) {
        await WriteProfile();
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", UnboundExpired());

        var recorder = new RefreshRecorder();
        await AuthFixtures.NewTokenStore(Config.Root, recorder, env).GetValidTokensForProfileAsync("default");

        return recorder.PostedTo;
    }

    /// <summary>With nothing overridden the profile's own server_url is the only endpoint on offer —
    /// the refresh reaches for the named profile's config, not the resolution the process started
    /// with, which for a token minted before server-binding names nothing.</summary>
    [Test]
    public async Task An_unoverridden_refresh_posts_to_the_profiles_own_server() =>
        await Assert.That(await RefreshUnder(ProfileOverrides.None))
                    .IsEqualTo(new Uri($"{ProfileUrl}/auth/refresh"));

    /// <summary>A named override still outranks the profile — the same precedence the resolution at
    /// startup applied, reached a second time for a profile that resolution never selected.</summary>
    [Test]
    public async Task A_named_override_still_outranks_the_profile() =>
        await Assert.That(await RefreshUnder(new ProfileOverrides("https://override.example", null)))
                    .IsEqualTo(new Uri("https://override.example/auth/refresh"));
}
