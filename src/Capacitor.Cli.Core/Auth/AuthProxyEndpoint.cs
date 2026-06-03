namespace Capacitor.Cli.Core.Auth;

public static class AuthProxyEndpoint {
    public const string DefaultUrl = "https://auth.kcap.ai";

    // KCAP_AUTH_PROXY_URL is an internal dev/test override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KCAP_AUTH_PROXY_URL") ?? DefaultUrl).TrimEnd('/');
}
