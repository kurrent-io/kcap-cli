namespace Capacitor.App.Services;

/// Superseded: an Unpin, another PinAsync, an account change or cancellation overtook the read.
public enum PinOutcome { Pinned, Unauthorized, Unavailable, Failed, Superseded }
