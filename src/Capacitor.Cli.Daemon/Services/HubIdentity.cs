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
}
