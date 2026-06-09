namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Pure formatting helper for <c>kcap whoami</c> output.
/// Extracted from Program.cs for unit-testability.
/// </summary>
public static class WhoAmIRenderer {
    /// <summary>
    /// Renders the account-type line for the active token.
    /// </summary>
    /// <returns>
    /// "Personal account: @{login}" for User tenants,
    /// "Organization: {login}" for Org tenants,
    /// "Username: {username}" when AccountType is not set (Auth0, GitHub-direct, or legacy tokens).
    /// </returns>
    public static string RenderAccountLine(StoredTokens tokens) {
        var login = !string.IsNullOrEmpty(tokens.AccountLogin) ? tokens.AccountLogin : tokens.GitHubUsername;

        return tokens.AccountType switch {
            "User"         => $"Personal account: @{login}",
            "Organization" => $"Organization: {login}",
            _              => $"Username: {tokens.GitHubUsername}"
        };
    }

    /// <summary>
    /// Renders the full <c>kcap whoami</c> output for an authenticated session.
    /// </summary>
    public static string Render(StoredTokens tokens, string serverUrl) {
        var accountLine  = RenderAccountLine(tokens);
        var remaining    = tokens.ExpiresAt - DateTimeOffset.UtcNow;
        var expiryText   = tokens.IsExpired
            ? "expired"
            : FormatRemaining(remaining);

        return $"{accountLine}\nProvider: {tokens.Provider}\nToken expires in {expiryText}\nServer: {serverUrl}";
    }

    static string FormatRemaining(TimeSpan remaining) {
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{(int)remaining.TotalMinutes}m";
    }
}
