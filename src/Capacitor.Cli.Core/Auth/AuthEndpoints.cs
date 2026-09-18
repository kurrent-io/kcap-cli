namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Where the fleet-wide auth services live, resolved once at the composition root.
///
/// <para>Both are a single value for the whole fleet rather than anything a tenant's
/// <c>/auth/config</c> carries, and both take an internal override so a dev build can be pointed at
/// a local stand-in. A value rather than a reader, so the lane that resolves an endpoint and the
/// command that reports it to the operator cannot answer differently.</para>
/// </summary>
public sealed record AuthEndpoints(string? ProxyOverride, string? SignupOverride) {
    public const string ProxyUrlVar  = "KCAP_AUTH_PROXY_URL";
    public const string SignupUrlVar = "KCAP_SIGNUP_URL";

    public const string DefaultProxyUrl  = "https://auth.kcap.ai";
    public const string DefaultSignupUrl = "https://capacitor.kurrent.io";

    /// <summary>Neither endpoint overridden.</summary>
    public static readonly AuthEndpoints Defaults = new(null, null);

    /// <summary>This process's endpoints. Call once, in <c>Main</c> or the composition root.</summary>
    public static AuthEndpoints FromEnvironment() => new(
        Environment.GetEnvironmentVariable(ProxyUrlVar),
        Environment.GetEnvironmentVariable(SignupUrlVar));

    /// <summary><c>KCAP_AUTH_PROXY_URL</c> is an internal dev/test override; not documented for end
    /// users.</summary>
    public string ProxyUrl => Resolve(ProxyOverride, DefaultProxyUrl);

    /// <summary><c>KCAP_SIGNUP_URL</c> is an internal dev/preview override; not documented for end
    /// users.</summary>
    public string SignupUrl => Resolve(SignupOverride, DefaultSignupUrl);

    static string Resolve(string? over, string fallback) => (over ?? fallback).TrimEnd('/');
}
