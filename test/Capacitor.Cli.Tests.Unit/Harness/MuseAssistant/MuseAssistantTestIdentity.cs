using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

static class MuseAssistantTestIdentity {
    /// <summary>A real identity over a real token store holding a token whose subject is <paramref name="userId"/>.</summary>
    public static async Task<MuseAssistantIdentity> SignedInAsync(ConfigRoot config, string serverUrl = "https://acme.kcap.ai", string userId = "user_01HXYZ") {
        var profiles = Resolutions.At(serverUrl, config);
        var tokens   = AuthFixtures.NewTokenStore(config);
        await tokens.SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = AccessTokens.ForSubject(userId), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "tester", Provider = "WorkOS"
        });

        return new MuseAssistantIdentity(profiles, tokens);
    }
}
