namespace Capacitor.Cli.Core.Auth;

// Keeps a valid WorkOS access token available across a long-running interactive flow.
//
// The create-a-tenant flow provisions, then polls for "active" for up to ~10 minutes — but a
// WorkOS AuthKit access token lives only ~5 minutes. Reusing the login-time token for the whole
// poll means it expires mid-wait; the server then 401s every status check, which the client
// swallows to a null "still provisioning", so the CLI spins silently until timeout even though
// the tenant went live. This refreshes the access token (public-client refresh_token grant, no
// secret) before it lapses so every status call carries a live token.
//
// Not thread-safe: the provisioning flow calls GetAsync serially.
public sealed class WorkOSTokenSource {
    readonly Func<string, CancellationToken, Task<WorkOSAuthResponse?>> refresh;
    readonly Func<DateTimeOffset>                                       now;
    readonly TimeSpan                                                   margin;

    string         accessToken;
    string?        refreshToken;
    DateTimeOffset expiresAt;

    public WorkOSTokenSource(
            string                                              accessToken,
            string?                                             refreshToken,
            Func<string, CancellationToken, Task<WorkOSAuthResponse?>> refresh,
            Func<DateTimeOffset>?                               now    = null,
            TimeSpan?                                           margin = null) {
        this.accessToken  = accessToken;
        this.refreshToken = refreshToken;
        this.refresh      = refresh;
        this.now          = now    ?? (() => DateTimeOffset.UtcNow);
        this.margin       = margin ?? TimeSpan.FromSeconds(60);
        expiresAt         = TokenStore.JwtExpiry(accessToken);
    }

    // The latest refresh token, rotated on each successful refresh. Callers that re-use the refresh
    // token after polling (the final org-switch) must read this, not the login-time value — WorkOS
    // rotates refresh tokens single-use, so the original is invalid once GetAsync has refreshed.
    public string? CurrentRefreshToken => refreshToken;

    // Returns a token expected to be valid for at least `margin`, refreshing first if the current
    // one is that close to (or past) expiry. A failed refresh degrades to the existing token — the
    // caller's HTTP call still surfaces the eventual 401 rather than this throwing mid-poll.
    public async Task<string> GetAsync(CancellationToken ct) {
        if (refreshToken is null) return accessToken;
        if (now() < expiresAt - margin) return accessToken;

        var refreshed = await refresh(refreshToken, ct);
        if (refreshed is { AccessToken.Length: > 0 }) {
            accessToken  = refreshed.AccessToken;
            refreshToken = refreshed.RefreshToken ?? refreshToken;
            expiresAt    = TokenStore.JwtExpiry(refreshed.AccessToken);
        }

        return accessToken;
    }
}
