namespace Capacitor.App.Services;

public delegate Task<SubagentEventsFetch> SubagentEventsReader(string sessionId, string agentId, long from, CancellationToken ct);
