using ReactiveUnit = System.Reactive.Unit;
using System.Collections.Immutable;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The call logs are appended from whatever thread the service under test dispatched its hub call
/// on and read from the test thread, so each is an immutable snapshot swapped atomically — a plain
/// List drops entries and throws mid-enumeration.
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
    ImmutableList<string> _stops = [], _chatSubscribes = [], _chatUnsubscribes = [], _accessWatches = [], _calls = [];

    public ImmutableList<string> Stops => Volatile.Read(ref _stops);
    public ImmutableList<string> ChatSubscribes => Volatile.Read(ref _chatSubscribes);
    public ImmutableList<string> ChatUnsubscribes => Volatile.Read(ref _chatUnsubscribes);
    public ImmutableList<string> AccessWatches => Volatile.Read(ref _accessWatches);
    public ImmutableList<string> Calls => Volatile.Read(ref _calls);

    static void Append(ref ImmutableList<string> log, string entry) =>
        ImmutableInterlocked.Update(ref log, static (l, e) => l.Add(e), entry);

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
        Append(ref _calls, $"stop:{agentId}");
        Append(ref _stops, agentId);
        return StopHandler(agentId);
    }

    public Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) {
        Append(ref _calls, $"chat:{sessionId}");
        Append(ref _chatSubscribes, sessionId);
        return SubscribeChatHandler(sessionId);
    }

    public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) {
        Append(ref _calls, $"unchat:{sessionId}");
        Append(ref _chatUnsubscribes, sessionId);
        return UnsubscribeChatHandler(sessionId);
    }

    public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) {
        Append(ref _calls, $"watch:{sessionId}");
        Append(ref _accessWatches, sessionId);
        return AccessWatchHandler(sessionId);
    }
}
