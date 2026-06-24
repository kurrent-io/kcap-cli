using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// WorkOS tenant discovery: authenticate org-less against the proxy's shared AuthKit app,
/// list the user's tenants via the proxy, let them pick, then org-switch into the chosen org
/// and save an org-scoped profile. The two browser/HTTP effects (org-less login, org-switch)
/// are injected so the orchestration (discover → pick → switch → save) is unit-testable;
/// production wiring passes <see cref="OAuthLoginFlow"/>'s loopback + switch helpers.
/// </summary>
public static class WorkOSDiscovery {
    const string WorkOSApiBase = "https://api.workos.com";

    public static async Task<int> RunAsync(
            string                                          proxyUrl,
            ProxyConfigResponse                             proxyConfig,
            IAuthProxyClient                                proxy,
            ITenantPicker                                   picker,
            Func<string, Task<WorkOSAuthResponse?>>         orglessLogin,   // arg: authorizeBase
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch) {    // args: refreshToken, organizationId
        if (string.IsNullOrEmpty(proxyConfig.WorkOSClientId)) {
            await Console.Error.WriteLineAsync("This server isn't configured for WorkOS sign-in.");

            return 1;
        }

        var authorizeBase = string.IsNullOrEmpty(proxyConfig.WorkOSAuthKitDomain)
            ? WorkOSApiBase
            : $"https://{proxyConfig.WorkOSAuthKitDomain}";

        var auth = await orglessLogin(authorizeBase);
        if (auth is null || string.IsNullOrEmpty(auth.RefreshToken)) {
            await Console.Error.WriteLineAsync("WorkOS sign-in failed.");

            return 1;
        }

        var result = await proxy.DiscoverWorkOSTenantsAsync(proxyUrl, auth.AccessToken);
        if (result.Error != DiscoveryError.None) {
            await Console.Error.WriteLineAsync(result.Error switch {
                DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
                DiscoveryError.TokenRejected    => "WorkOS rejected the authentication token. Please sign in again.",
                DiscoveryError.UpstreamError    => "Kurrent auth service returned an error. Try again later.",
                _                               => "Tenant discovery failed."
            });

            return 1;
        }

        if (result.Tenants.Length == 0) {
            await Console.Error.WriteLineAsync("No Capacitor tenants are linked to your account. Ask your admin to invite you.");

            return 1;
        }

        var picked = result.Tenants.Length == 1 ? result.Tenants[0] : picker.Pick(result.Tenants);
        if (picked is null) {
            await Console.Error.WriteLineAsync("No tenant selected.");

            return 1;
        }

        // Org-switch once into the chosen org. The resulting refresh token stays org-bound
        // (spike-confirmed), so later refreshes need no organization_id.
        var switched = await orgSwitch(auth.RefreshToken!, picked.OrganizationId ?? "");
        if (switched is null) {
            await Console.Error.WriteLineAsync($"Could not switch to organization {picked.Label}.");

            return 1;
        }

        var username = OAuthLoginFlow.WorkOSDisplayName(auth.User);

        var cfg = await AppConfig.LoadProfileConfig();
        cfg = TenantDiscovery.MergeProfiles(cfg, result.Tenants, picked);
        await AppConfig.SaveProfileConfig(cfg);

        await TokenStore.SaveAsync(
            picked.ProfileName,
            new StoredTokens {
                AccessToken    = switched.AccessToken,
                RefreshToken   = switched.RefreshToken,
                ExpiresAt      = TokenStore.JwtExpiry(switched.AccessToken),
                GitHubUsername = username,
                Provider       = AuthProvider.WorkOS,
                ClientId       = proxyConfig.WorkOSClientId
            });

        await Console.Out.WriteLineAsync($"Logged in as {username} → {picked.Label}");

        return 0;
    }
}
