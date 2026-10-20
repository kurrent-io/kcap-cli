using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Who a hub connection signs in as: the account a stored token was issued to, by the server that
/// issued it. A refresh changes the token and keeps the identity.</summary>
sealed record HubIdentity(string Account, string? Server, string Provider) {
    /// <summary>Null when the bearer comes from no stored token, or when there is no usable bearer at all.</summary>
    public static HubIdentity? Of(CredentialState state) =>
        state is { Status: AuthStatus.Ok, Resolution.Tokens: { } tokens }
            ? new(tokens.GitHubUsername, tokens.ServerUrl, tokens.Provider)
            : null;

    /// <summary>Whether a hub that negotiated as <paramref name="negotiated"/> should sign in again for
    /// <paramref name="current"/>: another account, a sign-out, or a token from another server. An expired
    /// token is left alone: a refresh that failed only for want of a network reads the same.</summary>
    public static bool Changed(HubIdentity? negotiated, CredentialState current) =>
        negotiated is not null && current.Status switch {
            AuthStatus.Ok                                        => Of(current) is { } now && now != negotiated,
            AuthStatus.NotAuthenticated or AuthStatus.WrongServer => true,
            _                                                    => false,
        };
}
