using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Detail is null on every failure; NotFound and Unauthorized name the two the caller treats
/// differently from lane loss.
public sealed record SessionDetailFetch(SessionDetailDto? Detail, bool NotFound = false, bool Unauthorized = false);

public delegate Task<SessionDetailFetch> SessionDetailReader(string sessionId, CancellationToken ct);
public delegate Task<ServerRespondOutcome> PermissionResponder(string sessionId, string requestId, PermissionResponsePayload payload, CancellationToken ct);
