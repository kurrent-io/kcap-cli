namespace Capacitor.Remote.Models;

public sealed record DiscardQueuedInputRequest(string AgentId, Guid DispatchId);
