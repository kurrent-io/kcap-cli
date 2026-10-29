using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Page is null on every failure; NotFound covers a subagent whose stream the server has not written yet, and
/// Failure says what else went wrong, for the log.
public sealed record SubagentEventsFetch(SubagentEventsDto? Page, bool NotFound = false, bool Unauthorized = false, string? Failure = null);
