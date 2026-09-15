using ReactiveUnit = System.Reactive.Unit;
using System.Collections.Immutable;
using System.Globalization;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Eventuous.SignalR;

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
    public readonly Subject<TerminalOutputFrame> TerminalOutputSubject = new();
    public readonly Subject<TerminalSize> TerminalDimensionsSubject = new();
    public Func<Task<IReadOnlyList<DaemonInfo>?>> DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([]);
    public Func<string, Task<HubCallOutcome>> StopHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> UnsubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    /// Returns the exception a tail throws at its first MoveNextAsync, or null to tail normally.
    public Func<string, ulong?, Exception?> TailHandler = (_, _) => null;
    public Func<string, Task<HubCallOutcome>> TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> UserInputHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> SpecialKeyHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    ImmutableList<string> _stops = [], _chatSubscribes = [], _chatUnsubscribes = [], _accessWatches = [], _calls = [];
    ImmutableList<(string Stream, ulong? From)> _tails = [];
    ImmutableList<string> _terminalSubscribes = [], _terminalUnsubscribes = [], _resizeReleases = [];
    ImmutableList<(string AgentId, int Cols, int Rows)> _resizes = [];
    ImmutableList<(string AgentId, string Text)> _userInputs = [];
    ImmutableList<(string AgentId, string Key)> _specialKeys = [];
    readonly Dictionary<string, Channel<StreamEventEnvelope>> _tailChannels = new(StringComparer.Ordinal);
    readonly Lock _tailLock = new();

    public ImmutableList<string> Stops => Volatile.Read(ref _stops);
    public ImmutableList<string> ChatSubscribes => Volatile.Read(ref _chatSubscribes);
    public ImmutableList<string> ChatUnsubscribes => Volatile.Read(ref _chatUnsubscribes);
    public ImmutableList<string> AccessWatches => Volatile.Read(ref _accessWatches);
    public ImmutableList<string> Calls => Volatile.Read(ref _calls);
    public ImmutableList<(string Stream, ulong? From)> Tails => Volatile.Read(ref _tails);
    public ImmutableList<string> TerminalSubscribes => Volatile.Read(ref _terminalSubscribes);
    public ImmutableList<string> TerminalUnsubscribes => Volatile.Read(ref _terminalUnsubscribes);
    public ImmutableList<(string AgentId, int Cols, int Rows)> Resizes => Volatile.Read(ref _resizes);
    public ImmutableList<string> ResizeReleases => Volatile.Read(ref _resizeReleases);
    public ImmutableList<(string AgentId, string Text)> UserInputs => Volatile.Read(ref _userInputs);
    public ImmutableList<(string AgentId, string Key)> SpecialKeys => Volatile.Read(ref _specialKeys);

    static void Append<T>(ref ImmutableList<T> list, T item) => ImmutableInterlocked.Update(ref list, static (l, i) => l.Add(i), item);

    public IObservable<ServerLaneStatus> Status => StatusSubject;
    public IObservable<ReactiveUnit> AgentInstancesChanged => AgentsChangedSubject;
    public IObservable<ReactiveUnit> DaemonsChanged => DaemonsChangedSubject;
    public IObservable<LaunchFailure> LaunchFailures => LaunchFailuresSubject;
    public IObservable<string> PermissionPending => PermissionPendingSubject;
    public IObservable<PermissionRespondedPing> PermissionResponded => PermissionRespondedSubject;
    public IObservable<ServerPermissionRequest> PermissionRequests => PermissionRequestsSubject;
    public IObservable<ServerElicitationRequest> ElicitationRequests => ElicitationsSubject;
    public IObservable<string> SessionAccessChanged => SessionAccessChangedSubject;
    public IObservable<TerminalOutputFrame> TerminalOutput => TerminalOutputSubject;
    public IObservable<TerminalSize> TerminalDimensions => TerminalDimensionsSubject;
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

    public async IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, [EnumeratorCancellation] CancellationToken ct) {
        Append(ref _tails, (stream, fromPosition));
        Append(ref _calls, $"tail:{stream}@{fromPosition?.ToString(CultureInfo.InvariantCulture) ?? "start"}");
        if (TailHandler(stream, fromPosition) is { } error) throw error;
        var channel = Channel.CreateUnbounded<StreamEventEnvelope>();
        lock (_tailLock) _tailChannels[stream] = channel;
        await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) yield return envelope;
    }

    /// Delivers an event to the live tail of its stream; a stream nobody tails buffers nothing.
    public void PushStreamEvent(StreamEventEnvelope envelope) {
        Channel<StreamEventEnvelope>? channel;
        lock (_tailLock) _tailChannels.TryGetValue(envelope.Stream, out channel);
        channel?.Writer.TryWrite(envelope);
    }

    /// Ends the live tail the way a dropped connection does: the enumeration completes.
    public void CloseTail(string stream) {
        Channel<StreamEventEnvelope>? channel;
        lock (_tailLock) _tailChannels.Remove(stream, out channel);
        channel?.Writer.TryComplete();
    }

    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"terminal:{agentId}");
        Append(ref _terminalSubscribes, agentId);
        return TerminalSubscribeHandler(agentId);
    }

    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"unterminal:{agentId}");
        Append(ref _terminalUnsubscribes, agentId);
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) {
        Append(ref _calls, $"resize:{agentId}:{cols}x{rows}");
        Append(ref _resizes, (agentId, cols, rows));
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"release:{agentId}");
        Append(ref _resizeReleases, agentId);
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct) {
        Append(ref _calls, $"input:{agentId}");
        Append(ref _userInputs, (agentId, text));
        return UserInputHandler(agentId);
    }

    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) {
        Append(ref _calls, $"key:{agentId}:{key}");
        Append(ref _specialKeys, (agentId, key));
        return SpecialKeyHandler(agentId);
    }
}
