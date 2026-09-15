using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Remote.Models;
using Eventuous.SignalR;
using Eventuous.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// The app's one long-lived server connection. Handlers are registered before StartAsync so no
/// broadcast can slip past a fresh connection; a closed or cold-failed connection re-dials on a
/// 1/2/5/10/30s ladder because SignalR's automatic reconnect covers neither cold-start failure
/// nor a close it decides not to retry.
public sealed class ServerConnectionService : IServerLane, ILaunchClient, IAsyncDisposable {
    public const string TeamClaimMissingNotice =
        "Signed-in token carries no team claim — server broadcasts may not reach this app.";

    static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    readonly string? _serverUrl;
    readonly Func<Task<string?>> _token;
    /// SignalR's own reconnect ladder, which a test shortens; null keeps the client's default.
    readonly TimeSpan[]? _reconnectDelays;
    readonly BehaviorSubject<ServerLaneStatus> _status = new(new(ServerLaneState.Dormant));
    readonly Subject<Unit> _agentsChanged = new();
    readonly Subject<Unit> _daemonsChanged = new();
    readonly Subject<LaunchFailure> _launchFailures = new();
    readonly Subject<string> _permissionPending = new();
    readonly Subject<PermissionRespondedPing> _permissionResponded = new();
    readonly Subject<ServerPermissionRequest> _permissionRequests = new();
    readonly Subject<ServerElicitationRequest> _elicitations = new();
    readonly Subject<string> _sessionAccessChanged = new();
    readonly Subject<PendingInputUpdate> _pendingInput = new();
    readonly Subject<TerminalOutputFrame> _terminalOutput = new();
    readonly Subject<TerminalSize> _terminalDimensions = new();
    // The subscription client of the live hub, replaced with it: it registers the StreamEvent
    // handler on the hub it wraps, so one per connection generation.
    volatile SignalRSubscriptionClient? _streams;
    readonly SemaphoreSlim _restartGate = new(1, 1);
    // One lock owns the whole lane lifecycle: the generation, the current loop's cancellation
    // source, and EVERY status publish. A generation names one admitted connect loop; a park, a
    // restart and teardown each advance it, so a publish carrying an older generation belongs to a
    // loop that has been superseded and is dropped rather than overwriting whatever superseded it.
    readonly Lock _lifecycleLock = new();
    int _generation;
    // The admitted loop's own source, so a park cancels the loop that was running when it decided
    // to park — never one a concurrent restart has since put in its place. Null between a restart
    // retiring the old loop and admitting the new one.
    CancellationTokenSource? _loopCts;
    // Set under the lock before the subjects are disposed, so no publish can be in flight when
    // they go.
    bool _closed;
    readonly CancellationTokenSource _lifetime = new();
    Task _loop = Task.CompletedTask;
    volatile HubConnection? _hub;

    public ServerConnectionService(ProfileContext? profiles, TokenStore tokenStore)
        : this(
            profiles?.Resolution.ServerUrl,
            profiles is null
                ? () => Task.FromResult<string?>(null)
                : async () => (await tokenStore.GetValidTokensForServerAsync(
                    profiles.Name, profiles.Resolution.ServerUrl!)).Tokens?.AccessToken) { }

    internal ServerConnectionService(
            string? serverUrl, Func<Task<string?>> accessTokenProvider, TimeSpan[]? reconnectDelays = null) {
        _serverUrl = string.IsNullOrEmpty(serverUrl) ? null : serverUrl.TrimEnd('/');
        _token = accessTokenProvider;
        _reconnectDelays = reconnectDelays;
    }

    public IObservable<ServerLaneStatus> Status => _status.AsObservable();
    public IObservable<Unit> AgentInstancesChanged => _agentsChanged.AsObservable();
    public IObservable<Unit> DaemonsChanged => _daemonsChanged.AsObservable();
    public IObservable<LaunchFailure> LaunchFailures => _launchFailures.AsObservable();
    public IObservable<string> PermissionPending => _permissionPending.AsObservable();
    public IObservable<PermissionRespondedPing> PermissionResponded => _permissionResponded.AsObservable();
    public IObservable<ServerPermissionRequest> PermissionRequests => _permissionRequests.AsObservable();
    public IObservable<ServerElicitationRequest> ElicitationRequests => _elicitations.AsObservable();
    public IObservable<string> SessionAccessChanged => _sessionAccessChanged.AsObservable();
    public IObservable<PendingInputUpdate> PendingInputChanged => _pendingInput.AsObservable();
    public IObservable<TerminalOutputFrame> TerminalOutput => _terminalOutput.AsObservable();
    public IObservable<TerminalSize> TerminalDimensions => _terminalDimensions.AsObservable();

    public void Start() {
        if (_serverUrl is null) return;
        _ = RestartAsync();
    }

    /// Same parked semantics as the 401-negotiate path in RunAsync (SignedOut, loop stopped) but
    /// triggered from outside it — RemoteAgentsService's onUnauthorized, when its own HTTP fetch
    /// hits a 401 the hub connection itself never saw. RestartAsync (wired to sign-in completion)
    /// is what revives it.
    public void ParkSignedOut() => ParkSignedOutCore(ifGeneration: null, detail: null);

    /// Parks only while `ifEpoch` is still the lane's current generation — the generation of the
    /// Connected status the caller's own decision was made under. RemoteAgentsService releases its
    /// cache lock before invoking onUnauthorized (taking it around the callback would invert it
    /// against this lane's lock), so by the time this runs a park, a restart or a teardown may have
    /// moved the lane on; re-validating here, atomically with the park itself, is what that
    /// callback could not do for itself.
    public void ParkSignedOut(int ifEpoch) => ParkSignedOutCore(ifEpoch, detail: null);

    void ParkSignedOutCore(int? ifGeneration, string? detail) {
        lock (_lifecycleLock) {
            if (_closed) return;
            if (ifGeneration is { } generation && generation != _generation) return;
            // Advancing first is what makes the park terminal: every publish the parked loop still
            // has in flight — its Connecting, its Retrying, a Connected waiting on DiagnoseAsync —
            // now carries a stale generation and is dropped. Cancelling the captured source under
            // the same lock stops the loop admitted at THIS generation and no other.
            _generation++;
            _status.OnNext(new(ServerLaneState.SignedOut, detail, Epoch: _generation));
            _loopCts?.Cancel();
        }
    }

    /// Retires the running loop and admits a new one under a fresh generation. A park landing while
    /// the old loop is still being awaited wins: the generation this restart reserved is no longer
    /// current, so no loop is admitted and the lane stays parked until the next restart.
    public async Task RestartAsync(CancellationToken ct = default) {
        if (_serverUrl is null || _lifetime.IsCancellationRequested) return;
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return;
            CancellationTokenSource? previous;
            int generation;
            lock (_lifecycleLock) {
                if (_closed) return;
                previous = _loopCts;
                // Unreachable to a park from here, so disposing it below cannot race one.
                _loopCts = null;
                _generation++;
                generation = _generation;
            }
            previous?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            previous?.Dispose();

            CancellationToken token;
            lock (_lifecycleLock) {
                if (_closed || generation != _generation) return;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loopCts = cts;
                token = cts.Token;
            }
            _loop = Task.Run(() => RunAsync(generation, token), CancellationToken.None);
        } finally {
            _restartGate.Release();
        }
    }

    async Task RunAsync(int generation, CancellationToken ct) {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            // A refused publish means this loop has been superseded — parked, restarted or torn
            // down — and has nothing left to dial for.
            if (!Publish(generation, new(ServerLaneState.Connecting))) return;
            HubConnection? hub = null;
            SignalRSubscriptionClient? streams = null;
            try {
                hub = Build();
                var capturedHub = hub;
                streams = new SignalRSubscriptionClient(hub);

                // Registered before StartAsync (SignalR supports that), so a close during the
                // DiagnoseAsync token read below — or during StartAsync itself — is still
                // observed, never lost with status stuck on a dead hub.
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask;  };
                hub.Reconnecting += _ => {
                    Publish(generation, new(ServerLaneState.Retrying, "reconnecting"));
                    return Task.CompletedTask;
                };
                hub.Reconnected += async _ => {
                    var (diagnostic, subject) = await DiagnoseAsync().ConfigureAwait(false);
                    PublishConnected(generation, capturedHub, diagnostic, subject);
                };

                await hub.StartAsync(ct).ConfigureAwait(false);
                _hub = hub;
                _streams = streams;
                attempt = 0;
                var (connectedDiagnostic, connectedSubject) = await DiagnoseAsync().ConfigureAwait(false);
                PublishConnected(generation, capturedHub, connectedDiagnostic, connectedSubject);

                Exception? closeReason;
                await using (ct.Register(() => closed.TrySetResult(null)))
                    closeReason = await closed.Task.ConfigureAwait(false);

                if (!ct.IsCancellationRequested)
                    Publish(generation, new(ServerLaneState.Retrying, closeReason?.Message));
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) when (IsUnauthorized(ex)) {
                // The credential is what kcap login repairs, not this loop — retrying a 401
                // negotiate forever would just burn the backoff ladder for no reason. The lane
                // parks here until RestartAsync (wired to sign-in completion) admits a fresh loop,
                // through the same park a delayed unauthorized fetch takes.
                ParkSignedOutCore(generation, ex.Message);
                return;
            } catch (Exception ex) {
                Publish(generation, new(ServerLaneState.Retrying, ex.Message));
            } finally {
                _streams = null;
                _hub = null;
                if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
                // The hub goes first: disposed, the client's courtesy unsubscribe finds a dead
                // connection and skips it (the library itself catches ObjectDisposedException),
                // so it never makes a server round-trip on the shutdown path. A failure here still
                // must not turn a loop exit into a fault.
                if (streams is not null) {
                    try { await streams.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
                }
            }

            if (ct.IsCancellationRequested) break;
            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// The one place a lifecycle status reaches subscribers: it carries the generation that
    /// produced it, and lands only while that generation is still current. False means the caller
    /// has been superseded.
    bool Publish(int generation, ServerLaneStatus status) {
        lock (_lifecycleLock) {
            if (_closed || generation != _generation) return false;
            _status.OnNext(status with { Epoch = generation });
            return true;
        }
    }

    HubConnection Build() {
        var builder = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/sessions", o => o.AccessTokenProvider = _token);
        var hub = (_reconnectDelays is { } delays ? builder.WithAutomaticReconnect(delays) : builder.WithAutomaticReconnect())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .Build();
        hub.On(HubBroadcasts.AgentInstancesChanged, () => _agentsChanged.OnNext(Unit.Default));
        hub.On(HubBroadcasts.DaemonsChanged, () => _daemonsChanged.OnNext(Unit.Default));
        hub.On<string, string>(HubBroadcasts.LaunchFailed, (agentId, reason) => _launchFailures.OnNext(new(agentId, reason)));
        hub.On<string>(HubBroadcasts.PermissionPending, _permissionPending.OnNext);
        hub.On<string, string?>(HubBroadcasts.PermissionResponded, (sid, rid) => _permissionResponded.OnNext(new(sid, rid)));
        hub.On<string, string, string?, JsonElement?, JsonElement?>(HubBroadcasts.PermissionRequested,
            (sid, rid, tool, input, options) => _permissionRequests.OnNext(ServerPermissionRequest.From(sid, rid, tool, input, options)));
        // JsonElement, not the typed array: binding the options to a record with required members
        // makes SignalR drop the WHOLE push over one malformed option, so it is parsed leniently
        // instead — an array that does not read as options is no options, which asks for free text.
        hub.On<string, string, string, JsonElement?, bool>(HubBroadcasts.AcpElicitationRequested,
            (sid, rid, prompt, options, multi) => _elicitations.OnNext(new(sid, rid, prompt, ServerPermissionRequest.ParseOptions(options) ?? [], multi)));
        hub.On<string>(HubBroadcasts.SessionAccessChanged, _sessionAccessChanged.OnNext);
        hub.On<string, string, JsonElement?>(HubBroadcasts.PendingInputChanged, (_, sessionId, items) => {
            if (ParseQueue(items) is { } queue) _pendingInput.OnNext(new(sessionId, queue));
        });
        hub.On<string, string>(HubBroadcasts.TerminalOutput, (agentId, base64) => _terminalOutput.OnNext(new(agentId, base64)));
        hub.On<string, int, int>(HubBroadcasts.TerminalDimensions, (agentId, cols, rows) => _terminalDimensions.OnNext(new(agentId, cols, rows)));
        return hub;
    }

    /// Connected additionally requires `hub` to still be the live connection and still be connected
    /// at publish time: DiagnoseAsync's await can outlast either, and a Connected published over a
    /// dead hub reaches downstream DistinctUntilChanged and swallows the real transition that
    /// followed it. Folded into the same locked check as the generation, so neither the Reconnecting
    /// handler nor a park can interleave with it.
    void PublishConnected(int generation, HubConnection hub, string? diagnostic, string? subject) {
        lock (_lifecycleLock) {
            if (!ReferenceEquals(_hub, hub) || hub.State != HubConnectionState.Connected) return;
            Publish(generation, new(ServerLaneState.Connected, Diagnostic: diagnostic, Subject: subject));
        }
    }

    async Task<(string? Diagnostic, string? Subject)> DiagnoseAsync() {
        try {
            var token = await _token().ConfigureAwait(false);
            if (token is null) return (null, null);
            var diagnostic = JwtClaims.TryGetString(token, "team_id") is null ? TeamClaimMissingNotice : null;
            return (diagnostic, JwtClaims.TryGetString(token, "sub"));
        } catch {
            return (null, null);
        }
    }

    public async Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        try {
            return await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons, ct).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    public Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.RequestStopAgent, ct, agentId);
    public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromChat, ct, sessionId);
    public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.RegisterSessionAccessWatch, ct, sessionId);

    /// One tail per stream at a time. The subscription client keys its registrations by stream
    /// name and removes the key unconditionally when an enumeration ends, so a replacement that
    /// registered before the previous enumeration finished would lose its registration to that
    /// cleanup and receive nothing: a new tail ends the previous one and waits for its cleanup
    /// first, and the replaced consumer sees an ended tail. Ending an enumeration also removes
    /// only the client-side registration — the server keeps pushing the stream to this
    /// connection until told otherwise — so a tail that ends with the hub up unsubscribes there.
    public async IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(
            string stream, ulong? fromPosition, [EnumeratorCancellation] CancellationToken ct) {
        var slot = new TailSlot(ct);
        TailSlot? previous;
        lock (_tailLock) {
            _tails.TryGetValue(stream, out previous);
            _tails[stream] = slot;
        }
        try {
            if (previous is not null) {
                previous.Cts.Cancel();
                await previous.Done.Task.ConfigureAwait(false);
            }
            var streams = _streams;
            var hub = _hub;
            if (streams is null || hub is not { State: HubConnectionState.Connected }) yield break;
            var source = streams.SubscribeAsync(stream, fromPosition, slot.Cts.Token).GetAsyncEnumerator(slot.Cts.Token);
            var refused = false;
            try {
                while (true) {
                    bool more;
                    try {
                        more = await source.MoveNextAsync().ConfigureAwait(false);
                    } catch (HubException) {
                        refused = true;
                        throw;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        throw;
                    } catch (Exception) {
                        // Lane loss is data, not an error: the next Connected re-tails from the last
                        // position, so the consumer sees an ended tail, never a transport fault. This
                        // also catches the library's own reconnect-exhausted close, which completes the
                        // channel with an OperationCanceledException carrying ITS OWN internal token —
                        // indistinguishable from ours by type, so only our own `ct` firing re-throws.
                        more = false;
                    }
                    if (!more) yield break;
                    yield return source.Current;
                }
            } finally {
                await source.DisposeAsync().ConfigureAwait(false);
                if (!refused && hub.State == HubConnectionState.Connected) {
                    try { await hub.InvokeAsync(SignalRSubscriptionMethods.Unsubscribe, stream, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        } finally {
            lock (_tailLock) { if (ReferenceEquals(_tails.GetValueOrDefault(stream), slot)) _tails.Remove(stream); }
            slot.Cts.Dispose();
            slot.Done.TrySetResult();
        }
    }

    readonly Dictionary<string, TailSlot> _tails = new(StringComparer.Ordinal);
    readonly Lock _tailLock = new();

    sealed class TailSlot(CancellationToken ct) {
        public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.SubscribeToTerminal, ct, agentId);
    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromTerminal, ct, agentId);
    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) => InvokeAsync(HubMethods.RequestResizeTerminal, ct, agentId, cols, rows);
    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.ReleaseResizeTerminal, ct, agentId);
    // Three arguments always: the method's arity is frozen and SignalR does not backfill a
    // missing trailing one.
    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct) => InvokeAsync(HubMethods.SendUserInput, ct, agentId, text, null);
    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) => InvokeAsync(HubMethods.SendSpecialKey, ct, agentId, key);

    /// Denied is the server's session-visibility refusal (HubException carrying WireTokens.
    /// SessionNotVisible); every other exception is Failed with its message, and a lane with no
    /// live hub answers NotConnected without dialing.
    async Task<HubCallOutcome> InvokeAsync(string method, CancellationToken ct, params object?[] args) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return HubCallOutcome.NotConnected;
        try {
            await hub.InvokeCoreAsync(method, args, ct).ConfigureAwait(false);
            return HubCallOutcome.Ok;
        } catch (OperationCanceledException) {
            throw;
        } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
            return HubCallOutcome.Denied(ex.Message);
        } catch (Exception ex) {
            return HubCallOutcome.Failed(ex.Message);
        }
    }

    /// The join answers with the session's queue; it rides the same stream as the pushes so a
    /// consumer sees one shape.
    public async Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) {
        var (outcome, snapshot) = await InvokeAsync<JsonElement?>(HubMethods.SubscribeToChat, ct, sessionId).ConfigureAwait(false);
        if (outcome.Result == HubCallResult.Ok && ParseQueue(snapshot) is { } queue) _pendingInput.OnNext(new(sessionId, queue));
        return outcome;
    }

    /// Null for a payload that is not a readable queue — never an empty one, which a consumer
    /// would read as the server having dropped every prompt it holds.
    static IReadOnlyList<QueuedInputItem>? ParseQueue(JsonElement? items) {
        if (items is not { } array || !array.IsArray) return null;
        try { return array.Deserialize(RemoteModelsJsonContext.Default.QueuedInputItemArray); }
        catch (JsonException) { return null; }
    }

    async Task<(HubCallOutcome Outcome, T? Result)> InvokeAsync<T>(string method, CancellationToken ct, params object?[] args) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return (HubCallOutcome.NotConnected, default);
        try {
            return (HubCallOutcome.Ok, await hub.InvokeCoreAsync<T>(method, args, ct).ConfigureAwait(false));
        } catch (OperationCanceledException) {
            throw;
        } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
            return (HubCallOutcome.Denied(ex.Message), default);
        } catch (Exception ex) {
            return (HubCallOutcome.Failed(ex.Message), default);
        }
    }

    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        if (_status.Value.State == ServerLaneState.SignedOut)
            return new LaunchOutcome(false, null, "Not signed in to the server.", Unauthorized: true);
        try {
            var hub = _hub;
            if (hub is not { State: HubConnectionState.Connected })
                return new LaunchOutcome(false, null, "Not connected to the server.");
            var agentId = await hub.InvokeAsync<string>(
                HubMethods.RequestLaunchAgentV2, LaunchPayload.For(request), ct).ConfigureAwait(false);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            return new LaunchOutcome(false, null, ex.Message, IsUnauthorized(ex));
        }
    }

    /// Walks the chain because SignalR surfaces the negotiate failure wrapped as often as bare.
    internal static bool IsUnauthorized(Exception ex) {
        for (Exception? e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) return true;
        }
        return false;
    }

    static async Task AwaitQuietly(Task t) {
        try { await t.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            CancellationTokenSource? loop;
            lock (_lifecycleLock) {
                _closed = true;
                _generation++;
                loop = _loopCts;
                _loopCts = null;
            }
            loop?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            loop?.Dispose();
        } finally {
            _restartGate.Release();
        }
        _status.Dispose();
        _agentsChanged.Dispose();
        _daemonsChanged.Dispose();
        _launchFailures.Dispose();
        _permissionPending.Dispose();
        _permissionResponded.Dispose();
        _permissionRequests.Dispose();
        _elicitations.Dispose();
        _sessionAccessChanged.Dispose();
        _pendingInput.Dispose();
        _terminalOutput.Dispose();
        _terminalDimensions.Dispose();
    }
}
