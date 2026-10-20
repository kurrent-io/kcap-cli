namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Where every WorkOS call of a known-server sign-in, refresh and org switch goes. A base other
/// than production is honoured only when it and the server are both loopback: a hosted tenant must
/// never be able to point a sign-in, or a refresh token, at a host of its choosing.
/// </summary>
public static class WorkOSApiBaseRule {
    /// <summary>The base for <paramref name="serverUrl"/>, which may have advertised
    /// <paramref name="advertised"/>. <paramref name="refused"/> is true only when a base other than
    /// production was advertised and is being ignored.</summary>
    public static string Resolve(string? serverUrl, string? advertised, out bool refused) {
        refused = false;

        if (string.IsNullOrWhiteSpace(advertised)) return WorkOSClient.ApiBase;

        var trimmed = advertised.TrimEnd('/');

        if (string.Equals(trimmed, WorkOSClient.ApiBase, StringComparison.OrdinalIgnoreCase)) return WorkOSClient.ApiBase;

        if (IsLoopbackHttp(serverUrl) && IsLoopbackHttp(trimmed)) return trimmed;

        refused = true;

        return WorkOSClient.ApiBase;
    }

    /// <summary>The base a stored token refreshes against, re-checked against the token's own
    /// server so an edited token file cannot redirect its refresh token.</summary>
    public static string ForTokens(StoredTokens tokens) => Resolve(tokens.ServerUrl, tokens.WorkOSApiBase, out _);

    /// <summary>What a token records: null for production, so the field is omitted from its file.</summary>
    public static string? Stamp(string apiBase) => apiBase == WorkOSClient.ApiBase ? null : apiBase;

    public static string RefusedWarning(string serverUrl, string advertised) =>
        $"Warning: {UnusableUrlDiagnostic.Sanitize(serverUrl)} advertised the WorkOS API at "
      + $"{UnusableUrlDiagnostic.Sanitize(advertised)}, which is honoured only when both are on this machine; "
      + $"signing in with {WorkOSClient.ApiBase} instead.";

    static bool IsLoopbackHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
     && uri.UserInfo.Length == 0
     && uri.IsLoopback;
}
