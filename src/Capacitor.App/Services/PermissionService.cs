using System.Reactive.Linq;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// Sole owner of the pending-permission cache, which holds both lanes keyed by
/// PendingPermissionRequest.Key. A local item is answerable only over the subscription that
/// delivered it, so every loss of that subscription — a closed attempt, a daemon without the
/// capability, a daemon gone — drops the local lane and hands back the server twins it was
/// shadowing, which carry handles that still work. The next Subscribed replay restores the local
/// cards and re-shadows the twins, so a daemon restart blinks those cards rather than leaving
/// unanswerable ones on screen.
/// One lock guards the tombstone set, the shadow set, the session
/// generations and every cache mutation: the tombstone test + upsert, the tombstone add + evict
/// (on an ack and on a Resolved push), the local-lane drops, the server-lane mutations and the
/// disposed flag. The stream loop, the status subscription, the session-agent feed and the
/// answering calls run on different continuations, and this lock is what makes the ordering hold.
/// Tombstones live for the service lifetime: request ids are never reused, so one can never
/// suppress a future request.
public sealed class PermissionService : IPermissionService {
    const string Capability = "permission/1";
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    readonly SourceCache<PendingPermissionRequest, string> _cache = new(p => p.Key);
    /// Server items a live local item claims: out of the cache, still known, so a lost local
    /// subscription can hand each back with its own answerable handle.
    readonly Dictionary<string, PendingPermissionRequest> _shadowed = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _sessionGenerations = new(StringComparer.Ordinal);
    readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly ILocalControlOps _ops;
    readonly Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> _subscribe;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly PermissionResponder _respond;
    readonly IDisposable _statusSub;
    readonly IDisposable? _agentsSub;
    IReadOnlyDictionary<string, string> _sessionAgents = new Dictionary<string, string>();
    CancellationTokenSource? _loopCts;
    bool _disposed;

    public PermissionService(
            IDaemonClientService service, ILocalControlOps ops,
            Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> subscribe,
            TimeProvider time, CancellationToken shutdownToken,
            PermissionResponder? respond = null,
            IObservable<IReadOnlyDictionary<string, string>>? sessionAgents = null) {
        _ops = ops; _subscribe = subscribe; _time = time; _shutdownToken = shutdownToken;
        _respond = respond ?? ((_, _, _, _) => Task.FromResult(new ServerRespondOutcome(ServerRespondKind.Unreachable, "not_signed_in")));
        _statusSub = service.Status.Subscribe(OnStatus);
        _agentsSub = sessionAgents?.Subscribe(OnSessionAgents);
    }

    public IObservable<IChangeSet<PendingPermissionRequest, string>> Pending => _cache.Connect();
    public IObservable<int> PendingCount => _cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        _cache.Connect()
            .QueryWhenChanged(q => Agents(q.Items))
            .StartWith(Agents(_cache.Items));
    public IObservable<PendingSummary> Summary =>
        _cache.Connect()
            .QueryWhenChanged(q => PendingSummary.From(q.Items))
            .StartWith(PendingSummary.From(_cache.Items));

    static IReadOnlySet<string> Agents(IEnumerable<PendingPermissionRequest> items) =>
        items.Select(p => p.AgentId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

    public Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
        var apply = answer == PermissionAnswer.AllowAlways ? ClaudePermissions.AlwaysAllow(target.ToolName) : (JsonElement?)null;
        if (target.Lane == PermissionLane.Local) {
            var decision = answer == PermissionAnswer.Deny ? PermissionResolveDecisions.Deny : PermissionResolveDecisions.Allow;
            return SendResolveAsync(target, new PermissionResolveDto(target.RequestId, decision, apply, null), ct);
        }
        return SendServerAsync(target, new PermissionResponsePayload {
            Behavior = answer == PermissionAnswer.Deny ? PermissionBehaviors.Deny : PermissionBehaviors.Allow, ApplyPermissions = apply,
        }, ct);
    }

    public Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
        if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
        var updated = ClaudeElicitation.ComposeAnswers(target.Questions, answers);
        return target.Lane == PermissionLane.Local
            ? SendResolveAsync(target, new PermissionResolveDto(target.RequestId, PermissionResolveDecisions.Allow, null, updated), ct)
            : SendServerAsync(target, new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow, UpdatedInput = updated }, ct);
    }

    public Task<PermissionResolveOutcome> AnswerAcpAsync(PendingPermissionRequest target, AcpAnswer answer, CancellationToken ct) {
        if (target.AcpQuestion is not { } question) throw new ArgumentException("not an ACP question", nameof(target));
        if (target.Lane != PermissionLane.Server) throw new ArgumentException("ACP questions are server-lane items", nameof(target));
        var labels = answer.SelectedOptionIds.Select(id => question.Options.FirstOrDefault(o => o.OptionId == id)?.Label ?? id).ToArray();
        var payload = answer.SelectedOptionIds.Count switch {
            0 => new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, FreeText = answer.FreeText },
            1 => new PermissionResponsePayload {
                Behavior = PermissionBehaviors.Answered, SelectedOptionId = answer.SelectedOptionIds[0], SelectedOptionLabel = labels[0], FreeText = answer.FreeText,
            },
            _ => new PermissionResponsePayload {
                Behavior = PermissionBehaviors.Answered, SelectedOptionIds = [.. answer.SelectedOptionIds], SelectedOptionLabels = labels, FreeText = answer.FreeText,
            },
        };
        return SendServerAsync(target, payload, ct);
    }

    public Task<PermissionResolveOutcome> PickOptionAsync(PendingPermissionRequest target, string optionId, CancellationToken ct) {
        var option = target.Options?.FirstOrDefault(o => o.OptionId == optionId) ?? throw new ArgumentException("not an offered option", nameof(optionId));
        if (target.Lane != PermissionLane.Server) throw new ArgumentException("ACP permissions are server-lane items", nameof(target));
        return SendServerAsync(target, new PermissionResponsePayload {
            Behavior = BehaviorFor(option.Kind), SelectedOptionId = option.OptionId, SelectedOptionLabel = option.Label,
        }, ct);
    }

    /// The daemon resolves the pick by OptionId; the behavior only tells it which way a missing id
    /// would have gone, so an unknown kind reads as allow.
    internal static string BehaviorFor(string? kind) =>
        kind is not null && (kind.Contains("reject", StringComparison.OrdinalIgnoreCase) || kind.Contains("deny", StringComparison.OrdinalIgnoreCase) || kind.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            ? PermissionBehaviors.Deny : PermissionBehaviors.Allow;

    public Task<PermissionResolveOutcome> WithdrawAsync(PendingPermissionRequest target, CancellationToken ct) =>
        target.Lane == PermissionLane.Local
            ? SendResolveAsync(target, new PermissionResolveDto(target.RequestId, PermissionResolveDecisions.Withdraw, null, null), ct)
            : Task.FromResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "withdraw_unsupported"));

    async Task<PermissionResolveOutcome> SendResolveAsync(PendingPermissionRequest target, PermissionResolveDto dto, CancellationToken ct) {
        PermissionAckDto ack;
        try {
            ack = await _ops.ResolvePermissionAsync(dto, ct).ConfigureAwait(false);
        } catch (LocalControlOpsException ex) {
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Reason);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: permission resolve failed unexpectedly: {ex.Message}");
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Message);
        }

        ConcludeLocal(target.RequestId);
        return new PermissionResolveOutcome(ack.Ok ? PermissionResolveKind.Applied : PermissionResolveKind.AlreadyDecided, ack.Error);
    }

    async Task<PermissionResolveOutcome> SendServerAsync(PendingPermissionRequest target, PermissionResponsePayload payload, CancellationToken ct) {
        var outcome = await _respond(target.SessionId, target.RequestId, payload, ct).ConfigureAwait(false);
        switch (outcome.Kind) {
            case ServerRespondKind.Applied:
                lock (_lock) { if (!_disposed) ConcludeServerKey(target.RequestId); }
                return new(PermissionResolveKind.Applied, null);
            case ServerRespondKind.NotPending:
                lock (_lock) { if (!_disposed) ConcludeServerKey(target.RequestId); }
                return new(PermissionResolveKind.AlreadyDecided, null);
            case ServerRespondKind.Rejected:
                return new(PermissionResolveKind.TransportFailure, outcome.Reason ?? "rejected");
            case ServerRespondKind.Unauthorized:
                return new(PermissionResolveKind.TransportFailure, "not_signed_in");
            default:
                return new(PermissionResolveKind.TransportFailure, outcome.Reason ?? "server_unreachable");
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _statusSub.Dispose();
        _agentsSub?.Dispose();
        StopLoop();
        _cache.Dispose();
    }

    void OnStatus(AttachStatus status) {
        if (status is { State: AttachState.Connected, Capabilities: not null } && status.Capabilities.Contains(Capability)) {
            StartLoop();
            return;
        }
        StopLoop();
        DropLocalLane();
    }

    void StartLoop() {
        CancellationTokenSource cts;
        lock (_lock) {
            if (_disposed || _loopCts is not null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _loopCts = cts;
        }
        _ = Task.Run(() => RunLoopAsync(cts));
    }

    void StopLoop() {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _loopCts; _loopCts = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task RunLoopAsync(CancellationTokenSource cts) {
        var ct = cts.Token;
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await foreach (var evt in _subscribe(ct).WithCancellation(ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested();
                        switch (evt) {
                            case PermissionStreamEvent.Subscribed: DropLocalLane(); break;
                            case PermissionStreamEvent.Pending p:  UpsertLocal(p.Request); break;
                            case PermissionStreamEvent.Resolved r: ConcludeLocal(r.Settlement.RequestId); break;
                        }
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap: permission subscription attempt failed: {ex.Message}");
                }
                // The attempt ended for a reason other than this loop's own cancellation, so the
                // socket that could answer the local cards is gone before the next one replays.
                DropLocalLane();
                try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        } finally {
            cts.Dispose();
        }
    }

    void UpsertLocal(PermissionPendingDto dto) {
        lock (_lock) {
            if (_disposed) return;
            var key = PendingPermissionRequest.KeyFor(PermissionLane.Local, dto.RequestId);
            if (_tombstones.Contains(key)) return;
            if (_cache.Lookup(key) is { HasValue: true, Value: var existing }) {
                // A mapping learned late is metadata: same instance, same card, so a draft answer
                // or an in-flight submit survives it.
                if (existing.ServerRequestId != dto.ServerRequestId) {
                    existing.ServerRequestId = dto.ServerRequestId;
                    _cache.Refresh(existing);
                    Shadow(dto.ServerRequestId);
                }
                return;
            }
            var item = new PendingPermissionRequest(dto);
            _cache.AddOrUpdate(item);
            Shadow(dto.ServerRequestId);
        }
    }

    // Caller holds _lock.
    void Shadow(string? serverRequestId) {
        if (serverRequestId is null) return;
        var key = PendingPermissionRequest.KeyFor(PermissionLane.Server, serverRequestId);
        if (_cache.Lookup(key) is { HasValue: true, Value: var twin }) { _shadowed[key] = twin; _cache.Remove(key); }
    }

    // Caller holds _lock.
    bool IsClaimed(string serverRequestId) => _cache.Items.Any(i => i.Lane == PermissionLane.Local && i.ServerRequestId == serverRequestId);

    void DropLocalLane() {
        lock (_lock) {
            if (_disposed) return;
            foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Local).ToList()) _cache.Remove(item.Key);
            foreach (var (key, twin) in _shadowed.ToList()) {
                _shadowed.Remove(key);
                if (_tombstones.Contains(key)) continue;
                // The map moved on while the twin was out of the cache, where nothing restamps it.
                twin.AgentId = _sessionAgents.GetValueOrDefault(twin.SessionId, "");
                _cache.AddOrUpdate(twin);
            }
        }
    }

    void ConcludeLocal(string requestId) {
        lock (_lock) {
            if (_disposed) return;
            var key = PendingPermissionRequest.KeyFor(PermissionLane.Local, requestId);
            _tombstones.Add(key);
            // The daemon has answered the hook, so the server copy is moot even if its relay fails.
            if (_cache.Lookup(key) is { HasValue: true, Value: var item } && item.ServerRequestId is { } srid) ConcludeServerKey(srid);
            _cache.Remove(key);
        }
    }

    // Caller holds _lock.
    void ConcludeServerKey(string serverRequestId) {
        var key = PendingPermissionRequest.KeyFor(PermissionLane.Server, serverRequestId);
        _tombstones.Add(key);
        _shadowed.Remove(key);
        _cache.Remove(key);
    }

    internal void UpsertServer(PendingPermissionRequest item) {
        lock (_lock) {
            if (_disposed || item.Lane != PermissionLane.Server || _tombstones.Contains(item.Key)) return;
            item.AgentId = _sessionAgents.GetValueOrDefault(item.SessionId, "");
            if (IsClaimed(item.RequestId)) { _shadowed[item.Key] = item; return; }
            if (_cache.Lookup(item.Key).HasValue) return; // a live card keeps its instance
            _cache.AddOrUpdate(item);
        }
    }

    /// A request id settles exactly one request, so it reaches across to the local claimant; a
    /// null id is the session-wide bulk clear, which is unversioned and therefore server-lane only.
    internal void SettleServer(string sessionId, string? requestId) {
        lock (_lock) {
            if (_disposed) return;
            if (requestId is { } rid) {
                ConcludeServerKey(rid);
                foreach (var local in _cache.Items.Where(i => i.Lane == PermissionLane.Local && i.ServerRequestId == rid).ToList()) {
                    _tombstones.Add(local.Key);
                    _cache.Remove(local.Key);
                }
                return;
            }
            _sessionGenerations[sessionId] = SessionGeneration(sessionId) + 1;
            foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId).ToList()) ConcludeServerKey(item.RequestId);
            foreach (var (_, twin) in _shadowed.Where(kv => kv.Value.SessionId == sessionId).ToList()) ConcludeServerKey(twin.RequestId);
        }
    }

    internal int SessionGeneration(string sessionId) { lock (_lock) return _sessionGenerations.GetValueOrDefault(sessionId); }

    /// The reconciliation's verdict for one session: adds what is missing, removes server items the
    /// stream no longer holds. Stale by generation means a clear ran meanwhile — drop the result.
    internal void ReplaceServerForSession(string sessionId, IReadOnlyList<PendingPermissionRequest> items, int generation) {
        lock (_lock) {
            if (_disposed || generation != SessionGeneration(sessionId)) return;
            var keep = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId && !keep.Contains(i.Key)).ToList())
                _cache.Remove(stale.Key);
            foreach (var stale in _shadowed.Where(kv => kv.Value.SessionId == sessionId && !keep.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                _shadowed.Remove(stale);
            foreach (var item in items) UpsertServer(item);
        }
    }

    /// Access revoked: the cards are unanswerable, but nothing is settled, so no tombstones.
    internal void DropServerForSession(string sessionId) {
        lock (_lock) {
            if (_disposed) return;
            foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId).ToList()) _cache.Remove(item.Key);
            foreach (var key in _shadowed.Where(kv => kv.Value.SessionId == sessionId).Select(kv => kv.Key).ToList()) _shadowed.Remove(key);
        }
    }

    internal void ClearServerLane() {
        lock (_lock) {
            if (_disposed) return;
            foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server).ToList()) _cache.Remove(item.Key);
            _shadowed.Clear();
        }
    }

    void OnSessionAgents(IReadOnlyDictionary<string, string> map) {
        lock (_lock) {
            if (_disposed) return;
            _sessionAgents = map;
            foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server).ToList()) {
                var agent = map.GetValueOrDefault(item.SessionId, "");
                if (item.AgentId == agent) continue;
                item.AgentId = agent;
                _cache.Refresh(item);
            }
        }
    }
}
