using System.Text.Json;

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
public sealed class WorkOSTokenSource(
        string                                                     accessToken,
        string?                                                    refreshToken,
        Func<string, CancellationToken, Task<WorkOSAuthResponse?>> refresh,
        Func<DateTimeOffset>?                                      now    = null,
        TimeSpan?                                                  margin = null
    ) {
    readonly Func<DateTimeOffset> _now    = now    ?? (() => DateTimeOffset.UtcNow);
    readonly TimeSpan             _margin = margin ?? TimeSpan.FromSeconds(60);

    string         _accessToken  = accessToken;
    string?        _refreshToken = refreshToken;
    DateTimeOffset _expiresAt    = TokenStore.JwtExpiry(accessToken);

    // The latest refresh token, rotated on each successful refresh. Callers that re-use the refresh
    // token after polling (the final org-switch) must read this, not the login-time value — WorkOS
    // rotates refresh tokens single-use, so the original is invalid once GetAsync has refreshed.
    public string? CurrentRefreshToken => _refreshToken;

    // Returns a token expected to be valid for at least `margin`, refreshing first if the current
    // one is that close to (or past) expiry. A failed refresh — whether the delegate returns null or
    // throws a transient network/JSON error — degrades to the existing token so a blip can't abort a
    // long provisioning poll; the caller's HTTP call still surfaces the eventual 401, and the next
    // tick retries. A genuine cancellation (via ct) is not swallowed.
    public async Task<string> GetAsync(CancellationToken ct) {
        if (_refreshToken is null || _now() < _expiresAt - _margin) return _accessToken;

        WorkOSAuthResponse? refreshed;

        try {
            refreshed = await refresh(_refreshToken, ct);
        } catch (Exception e) when (IsTransient(e) && !ct.IsCancellationRequested) {
            return _accessToken;
        }

        if (refreshed is { AccessToken.Length: > 0 }) {
            _accessToken  = refreshed.AccessToken;
            _refreshToken = refreshed.RefreshToken ?? _refreshToken;
            _expiresAt    = TokenStore.JwtExpiry(refreshed.AccessToken);
        }

        return _accessToken;
    }

    // Network / timeout / unreadable-body failures the refresh degrades on. OperationCanceledException
    // is included as an HttpClient timeout; a real user/ct cancel is excluded by the caller's
    // !ct.IsCancellationRequested guard so it still propagates. Mirrors TenantProvisioningClient.
    static bool IsTransient(Exception e) =>
        e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;
}
