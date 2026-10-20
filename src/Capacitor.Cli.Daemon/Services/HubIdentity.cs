using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Who a hub connection signs in as: the account a stored token was issued to, by the server that
/// issued it. A refresh changes the token and keeps the identity.</summary>
sealed record HubIdentity(string Account, string? Server, string Provider) {
    /// <summary>Null when the bearer comes from no stored token, or when there is no usable bearer at all.
    /// The account is the token's own subject where it has one: a WorkOS token stores a display name,
    /// which two people can share.</summary>
    public static HubIdentity? Of(CredentialState state) =>
        state is { Status: AuthStatus.Ok, Resolution.Tokens: { } tokens }
            ? new(Subject(tokens.AccessToken) ?? tokens.GitHubUsername, tokens.ServerUrl, tokens.Provider)
            : null;

    /// <summary>Whether a hub that negotiated as <paramref name="negotiated"/> should sign in again for
    /// <paramref name="current"/>: another account, a sign-out, or a token from another server. An expired
    /// token is left alone: a refresh that failed only for want of a network reads the same.</summary>
    public static bool Changed(HubIdentity? negotiated, CredentialState current) =>
        negotiated is not null && current.Status switch {
            AuthStatus.Ok                                        => Of(current) is { } now && Differs(now, negotiated),
            AuthStatus.NotAuthenticated or AuthStatus.WrongServer => true,
            _                                                    => false,
        };

    // A pre-upgrade token names no issuing server until a refresh stamps one, so a server missing on
    // either side matches.
    static bool Differs(HubIdentity a, HubIdentity b) =>
        a.Account != b.Account || a.Provider != b.Provider || a.Server is not null && b.Server is not null && a.Server != b.Server;

    // Read without validation, as TokenStore.JwtExpiry reads `exp`: only the server decides what a token proves.
    static string? Subject(string token) {
        try {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));

            return doc.RootElement.Str("sub");
        } catch {
            return null;
        }
    }
}
