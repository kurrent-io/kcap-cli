namespace Capacitor.Cli.Core.Auth;

public static class ProvisioningEndpoint {
    public const string DefaultUrl = "https://capacitor.kurrent.io";

    // KCAP_SIGNUP_URL is an internal dev/preview override; not documented for end users.
    public static string Url =>
        (Environment.GetEnvironmentVariable("KCAP_SIGNUP_URL") ?? DefaultUrl).TrimEnd('/');
}
