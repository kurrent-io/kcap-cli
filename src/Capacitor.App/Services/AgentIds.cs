namespace Capacitor.App.Services;

/// Id shapes differ across the stack and across daemon versions: the server hub returns dashed
/// Guids while a daemon keys its status cache on the "N" form or a short id. A Guid in any format
/// normalizes to "N"; any other non-empty id passes through verbatim to match what the daemon
/// sent. Only a null or blank id is unusable.
public static class AgentIds {
    public static string? Normalize(string? agentId) =>
        Guid.TryParse(agentId, out var parsed) ? parsed.ToString("N")
        : string.IsNullOrWhiteSpace(agentId) ? null
        : agentId;
}
