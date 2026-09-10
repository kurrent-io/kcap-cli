using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// The no-server fallback for a composition root with no live remote lane (most tests, and any
/// caller that builds its own AgentDirectory without one): never reports a remote agent or
/// daemon, so the directory it feeds stays a pure mirror of the local daemon.
internal sealed class NoRemoteAgents : IRemoteAgentsService {
    public IObservableCache<AgentInstanceDto, string> Agents { get; } =
        new SourceCache<AgentInstanceDto, string>(a => a.AgentId).AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; } = Observable.Return<IReadOnlyList<DaemonInfo>>([]);
}

/// Paired with <see cref="NoRemoteAgents"/>: a lane that never connects.
internal sealed class NoServerLane : IServerLane {
    public IObservable<ServerLaneStatus> Status { get; } = Observable.Return(new ServerLaneStatus(ServerLaneState.Dormant));
    public IObservable<Unit> AgentInstancesChanged => Observable.Never<Unit>();
    public IObservable<Unit> DaemonsChanged => Observable.Never<Unit>();
    public IObservable<LaunchFailure> LaunchFailures => Observable.Never<LaunchFailure>();
    public IObservable<string> PermissionPending => Observable.Never<string>();
    public IObservable<PermissionRespondedPing> PermissionResponded => Observable.Never<PermissionRespondedPing>();
    public IObservable<ServerPermissionRequest> PermissionRequests => Observable.Never<ServerPermissionRequest>();
    public IObservable<ServerElicitationRequest> ElicitationRequests => Observable.Never<ServerElicitationRequest>();
    public IObservable<string> SessionAccessChanged => Observable.Never<string>();
    public Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DaemonInfo>?>(null);
    public Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct) =>
        Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(HubCallOutcome.NotConnected);
}
