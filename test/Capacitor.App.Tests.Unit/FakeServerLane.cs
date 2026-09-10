using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

sealed class FakeServerLane : IServerLane {
    public readonly BehaviorSubject<ServerLaneStatus> StatusSubject = new(new(ServerLaneState.Dormant));
    public readonly Subject<ReactiveUnit> AgentsChangedSubject = new();
    public readonly Subject<ReactiveUnit> DaemonsChangedSubject = new();
    public readonly Subject<LaunchFailure> LaunchFailuresSubject = new();
    public readonly Subject<string> PermissionPendingSubject = new();
    public readonly Subject<PermissionRespondedPing> PermissionRespondedSubject = new();
    public readonly Subject<ServerPermissionRequest> PermissionRequestsSubject = new();
    public readonly Subject<ServerElicitationRequest> ElicitationsSubject = new();
    public readonly Subject<string> SessionAccessChangedSubject = new();
    public Func<Task<IReadOnlyList<DaemonInfo>?>> DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([]);
    public Func<string, Task<HubCallOutcome>> StopHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> UnsubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public readonly List<string> Stops = [], ChatSubscribes = [], ChatUnsubscribes = [], AccessWatches = [];
    public readonly List<string> Calls = [];

    public IObservable<ServerLaneStatus> Status => StatusSubject;
    public IObservable<ReactiveUnit> AgentInstancesChanged => AgentsChangedSubject;
    public IObservable<ReactiveUnit> DaemonsChanged => DaemonsChangedSubject;
    public IObservable<LaunchFailure> LaunchFailures => LaunchFailuresSubject;
    public IObservable<string> PermissionPending => PermissionPendingSubject;
    public IObservable<PermissionRespondedPing> PermissionResponded => PermissionRespondedSubject;
    public IObservable<ServerPermissionRequest> PermissionRequests => PermissionRequestsSubject;
    public IObservable<ServerElicitationRequest> ElicitationRequests => ElicitationsSubject;
    public IObservable<string> SessionAccessChanged => SessionAccessChangedSubject;
    public Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) => DaemonsHandler();

    public Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct) {
        Calls.Add($"stop:{agentId}");
        Stops.Add(agentId);
        return StopHandler(agentId);
    }

    public Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) {
        Calls.Add($"chat:{sessionId}");
        ChatSubscribes.Add(sessionId);
        return SubscribeChatHandler(sessionId);
    }

    public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) {
        Calls.Add($"unchat:{sessionId}");
        ChatUnsubscribes.Add(sessionId);
        return UnsubscribeChatHandler(sessionId);
    }

    public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) {
        Calls.Add($"watch:{sessionId}");
        AccessWatches.Add(sessionId);
        return AccessWatchHandler(sessionId);
    }
}
