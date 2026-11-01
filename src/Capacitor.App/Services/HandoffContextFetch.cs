using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Unavailable is a session the server hides from the caller or does not hold; Context is set only on Ok.
public sealed record HandoffContextFetch(ServerReadOutcome Outcome, HandoffContextDto? Context);
