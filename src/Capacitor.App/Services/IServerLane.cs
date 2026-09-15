using Eventuous.SignalR;

namespace Capacitor.App.Services;

public enum ServerLaneState { Dormant, Connecting, Connected, Retrying, SignedOut }

/// Diagnostic is the silent-deafness notice text, or null. Subject is the signed-in account's
/// "sub" claim on a Connected status, for detecting a re-auth as a different account. Epoch
/// identifies the connection generation that published this status: it advances on every restart
/// and every park, so a caller that captures it off one Connected status can hand it back to
/// re-validate a delayed decision (a park request, say) against whatever the lane is running now.
public sealed record ServerLaneStatus(
    ServerLaneState State, string? Detail = null, string? Diagnostic = null, string? Subject = null,
    int Epoch = 0);

public sealed record LaunchFailure(string AgentId, string Reason);

public interface IServerLane {
    /// Replay-1; initial value (Dormant) published synchronously at construction.
    IObservable<ServerLaneStatus> Status { get; }
    IObservable<System.Reactive.Unit> AgentInstancesChanged { get; }
    IObservable<System.Reactive.Unit> DaemonsChanged { get; }
    IObservable<LaunchFailure> LaunchFailures { get; }
    IObservable<string> PermissionPending { get; }
    IObservable<PermissionRespondedPing> PermissionResponded { get; }
    IObservable<ServerPermissionRequest> PermissionRequests { get; }
    IObservable<ServerElicitationRequest> ElicitationRequests { get; }
    IObservable<string> SessionAccessChanged { get; }
    /// Null when the lane has no live connection right now.
    Task<IReadOnlyList<Capacitor.Remote.Models.DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct);
    Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct);
    Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct);
    Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct);
    IObservable<TerminalOutputFrame> TerminalOutput { get; }
    IObservable<TerminalSize> TerminalDimensions { get; }
    /// A live tail from the position after `fromPosition` (null: the start). Completes when the
    /// connection closes or the stream is subscribed again; throws HubException on denial at the
    /// first MoveNextAsync; yields nothing while the lane has no live hub.
    IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, CancellationToken ct);
    Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct);
    Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct);
    Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct);
}
