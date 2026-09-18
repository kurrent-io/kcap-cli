namespace Capacitor.App.Services;

public enum HubCallResult { Ok, NotConnected, Denied, Failed }

/// One hub invoke's verdict. Denied is the server's session-visibility refusal; every other
/// exception is Failed with its message, and a lane with no live hub answers NotConnected
/// without dialing.
public sealed record HubCallOutcome(HubCallResult Result, string? Reason = null) {
    public static readonly HubCallOutcome Ok = new(HubCallResult.Ok);
    public static readonly HubCallOutcome NotConnected = new(HubCallResult.NotConnected);
    public static HubCallOutcome Denied(string reason) => new(HubCallResult.Denied, reason);
    public static HubCallOutcome Failed(string reason) => new(HubCallResult.Failed, reason);
}
