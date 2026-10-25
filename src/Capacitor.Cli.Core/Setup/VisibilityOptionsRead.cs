namespace Capacitor.Cli.Core.Setup;

/// <summary>What the server said about the stops to offer. With no <see cref="Offer"/> the caller offers every
/// stop, as it does when the server answers 404 for the route; <see cref="Problem"/> says why when the server should
/// have answered and did not</summary>
public readonly record struct VisibilityOptionsRead(VisibilityOptionsResponse? Offer, string? Problem) {
    public static VisibilityOptionsRead NotServed => new(null, null);

    public static VisibilityOptionsRead Failed(string problem) => new(null, problem);
}
