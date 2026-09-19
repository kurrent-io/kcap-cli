namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What a sign-in found, for a caller that has to ask someone which workspace to use before it can
/// name one. Nothing here was published: the commit boundary is not entered on this route.
/// </summary>
/// <param name="CanCreate">No workspace was found, on the one lane that can create one. Not a
/// promise that creating will succeed: a workspace this account already asked for and that is still
/// being made is only learned by the create call itself.</param>
public sealed record DiscoveryReport(
        DiscoveredTenant[] Tenants,
        string             Provider,
        bool               CanCreate,
        string?            Error = null) {
    public static DiscoveryReport Failure(string provider, string error) =>
        new([], provider, CanCreate: false, error);

    public static DiscoveryReport Cancelled(string provider) => Failure(provider, "Discovery was cancelled.");
}
