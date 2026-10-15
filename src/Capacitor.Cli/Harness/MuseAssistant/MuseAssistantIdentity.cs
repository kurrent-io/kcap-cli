using System.Globalization;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Resolves the tenant and user every command binds its state to, from the active profile and
/// the stored token alone. No request: an identity that depended on the server would move while the
/// server is down, and the state directory with it.</summary>
public sealed class MuseAssistantIdentity(ProfileContext profiles, TokenStore tokens) {
    public async Task<(MuseAssistantPrincipal? Principal, string? Problem)> ResolveAsync(CancellationToken ct) {
        if (profiles.Resolution.ServerUrl is not { Length: > 0 } server)
            return (null, "No server configured. Run `kcap setup`.");

        var stored = await tokens.LoadForProfileAsync(profiles.Name, ct);
        var userId = stored?.AccessToken is { Length: > 0 } token ? UserIdOf(token) : null;

        if (userId is null) return (null, "Not signed in. Run `kcap login --device`.");

        return (new MuseAssistantPrincipal(AppConfig.NormalizeUrl(server), userId), null);
    }

    /// <summary>What the server's own user-id resolution yields for this bearer: GitHub-issued tokens carry
    /// <c>github_id</c> and become <c>github:{id}</c>; WorkOS tokens' user id is the subject verbatim.</summary>
    public static string? UserIdOf(string accessToken) =>
        long.TryParse(JwtClaims.TryGetString(accessToken, "github_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var gitHubId) ? $"github:{gitHubId.ToString(CultureInfo.InvariantCulture)}"
      : JwtClaims.TryGetString(accessToken, "sub") is { Length: > 0 } sub ? sub
      : null;
}
