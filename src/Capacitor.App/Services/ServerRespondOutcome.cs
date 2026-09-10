namespace Capacitor.App.Services;

public enum ServerRespondKind { Applied, NotPending, Rejected, Unauthorized, Unreachable }

/// NotPending is the route's 404: settled elsewhere, drop the card. Rejected is a 400 with the
/// server's reason. Unauthorized and Unreachable leave the request pending.
public sealed record ServerRespondOutcome(ServerRespondKind Kind, string? Reason = null);
