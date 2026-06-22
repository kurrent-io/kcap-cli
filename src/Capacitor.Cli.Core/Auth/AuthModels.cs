using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// GET /auth/config — all fields optional since shape varies by provider
public record AuthDiscoveryResponse {
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("authkit_domain")]
    public string? AuthKitDomain { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("github_client_id")]
    public string? GithubClientId { get; init; }

    [JsonPropertyName("token_exchange_url")]
    public string? TokenExchangeUrl { get; init; }

    // Server-mediated GitHub code-exchange endpoint. Required for the localhost
    // browser flow because GitHub Apps need client_secret on POST /login/oauth/access_token,
    // and the CLI can't ship a secret. When null, the CLI uses device flow.
    [JsonPropertyName("github_code_exchange_url")]
    public string? GithubCodeExchangeUrl { get; init; }
}

// POST {github_code_exchange_url} — CLI → server. Server adds client_id + client_secret
// and forwards to https://github.com/login/oauth/access_token.
public record GitHubCodeExchangeRequest {
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("code_verifier")]
    public required string CodeVerifier { get; init; }

    [JsonPropertyName("redirect_uri")]
    public required string RedirectUri { get; init; }
}

// POST /auth/token request
public record TokenExchangeRequest {
    [JsonPropertyName("github_access_token")]
    public string? GithubAccessToken { get; init; }
}

// POST /auth/token response
public record TokenExchangeResponse {
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";
}

// POST /auth/token error body (any failing status)
public record AuthErrorResponse {
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// GitHub Device Flow: POST https://github.com/login/device/code
public record GitHubDeviceCodeResponse {
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = "";

    [JsonPropertyName("interval")]
    public int Interval { get; init; } = 5;
}

// GitHub Device Flow: POST https://github.com/login/oauth/access_token
public record GitHubTokenResponse {
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// WorkOS: POST /user_management/authenticate (PKCE — no client secret)
public record WorkOSAuthResponse {
    [JsonPropertyName("user")]
    public WorkOSUserInfo? User { get; init; }

    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; init; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }
}

public record WorkOSUserInfo {
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }
}

// POST /auth/refresh request
public record RefreshTokenRequest {
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }
}

// Auth proxy: GET /config
public sealed record ProxyConfigResponse {
    [JsonPropertyName("github_client_id")]         public string  GitHubClientId        { get; init; } = "";

    // Proxy-mediated GitHub code-exchange endpoint. When null, --discover falls back
    // to device flow because the CLI can't talk to GitHub's token endpoint directly
    // for a GitHub App without client_secret.
    [JsonPropertyName("github_code_exchange_url")] public string? GitHubCodeExchangeUrl { get; init; }
}

// Auth proxy: POST /discover-tenants response item
public sealed record DiscoveredTenant {
    [JsonPropertyName("org_id")]    public long   OrgId    { get; init; }
    [JsonPropertyName("org_login")] public string OrgLogin { get; init; } = "";
    [JsonPropertyName("origin")]    public string Origin   { get; init; } = "";
}

public enum DiscoveryError {
    None,
    ProxyUnreachable,
    TokenRejected,
    UpstreamError
}

public sealed record DiscoveryResult(DiscoveredTenant[] Tenants, DiscoveryError Error);
