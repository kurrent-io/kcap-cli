using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct PermissionSettlement(PermissionDecision Decision, string Outcome, string Source);

internal abstract record PermissionStreamItem {
    public sealed record Pending(PermissionPendingDto Dto) : PermissionStreamItem;
    public sealed record Resolved(PermissionResolvedDto Dto) : PermissionStreamItem;
}

internal static class PermissionSettlements {
    public const string Allow = "allow", Deny = "deny", Withdrawn = "withdrawn";
    public const string SourceApp = "app", SourceServer = "server", SourceAgentGone = "agent_gone",
                        SourceNoUi = "no_ui", SourceDaemonShutdown = "daemon_shutdown",
                        SourcePolicy = "policy", SourceToolSettled = "tool_settled";
    public static readonly PermissionDecision DenyDecision = new("deny", null, null);
}

/// The single claim point for a hosted permission request. Every settlement — the app, the
/// server's push, an agent's withdrawal, the no-UI deny, the shutdown claim — goes through
/// TrySettle under one gate that replay/registration also takes, so a subscriber observes a
/// request as nothing, or Pending then Resolved, never Pending alone. The withdrawn set is
/// service-lifetime: agent ids are never reused, so it can never suppress a future agent.
internal sealed class PermissionPromptBroker(TimeProvider? time = null) {
    sealed record Entry(PermissionPendingDto Dto, TaskCompletionSource<PermissionSettlement> Tcs, string? SubagentId);

    /// The hook-reporting scope a prompt or a completion belongs to.
    readonly record struct Scope(string AgentId, string SessionId, string? SubagentId);

    /// A completion report a scope is still owed: the call ran, or will, and its hook reports it
    /// once. Lapses a grace period after its turn ends, since no report can be in flight by then.
    sealed class Owed(Scope scope, string toolName, JsonElement? toolInput) {
        public Scope Scope { get; } = scope;
        public string ToolName { get; } = toolName;
        public JsonElement? ToolInput { get; } = toolInput;
        public DateTimeOffset? LapsesAt { get; set; }
    }

    /// The hook that reports a completion has a 5s timeout and the relay a 1s cap, so a report
    /// still in flight when the turn ends lands well inside this.
    internal static readonly TimeSpan OwedGraceAfterTurn = TimeSpan.FromSeconds(30);
    internal const int MaxOwedPerScope = 256;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    readonly List<Owed> _owed = [];
    /// Scopes whose ledger overflowed, so their fallback is off; the value is the lapse a turn's
    /// end set, null until a turn ends with nothing discarded since.
    readonly Dictionary<Scope, DateTimeOffset?> _suspended = new();
    readonly ConcurrentDictionary<Guid, Channel<PermissionStreamItem>> _subscribers = new();
    readonly HashSet<string> _withdrawnAgents = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    internal int OwedCountForTest { get { lock (_gate) return _owed.Count; } }

    /// <param name="subagentId">The vendor's id for the subagent whose tool call this is, null for
    /// the main agent's own. Kept off the wire: only <see cref="WithdrawTurn"/> reads it.</param>
    public Task<PermissionSettlement> Register(PermissionPendingDto dto, string? subagentId = null) {
        lock (_gate) {
            if (_withdrawnAgents.Contains(dto.AgentId))
                return Task.FromResult(new PermissionSettlement(
                    PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone));

            // Completed while the gate is held: a continuation running inline would re-enter it.
            var entry = new Entry(dto, new(TaskCreationOptions.RunContinuationsAsynchronously), subagentId);
            if (!_pending.TryAdd(dto.RequestId, entry))
                throw new InvalidOperationException($"permission request {dto.RequestId} is already pending");
            Broadcast(new PermissionStreamItem.Pending(dto));
            return entry.Tcs.Task;
        }
    }

    public bool TrySettle(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return SettleLocked(requestId, decision, outcome, source);
    }

    public bool TrySettleIfNoSubscriber(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return _subscribers.IsEmpty && SettleLocked(requestId, decision, outcome, source);
    }

    /// Records the server's id for a still-pending request and re-broadcasts its Pending so a
    /// subscriber can pair the two lanes. False once settled: a settled request never re-appears.
    public bool TryCorrelate(string requestId, string serverRequestId) {
        lock (_gate) {
            if (!_pending.TryGetValue(requestId, out var entry)) return false;
            var updated = entry with { Dto = entry.Dto with { ServerRequestId = serverRequestId } };
            _pending[requestId] = updated;
            Broadcast(new PermissionStreamItem.Pending(updated.Dto));
            return true;
        }
    }

    public (Guid id, ChannelReader<PermissionStreamItem> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<PermissionStreamItem>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) {
            foreach (var e in _pending.Values) ch.Writer.TryWrite(new PermissionStreamItem.Pending(e.Dto));
            _subscribers[id] = ch;
        }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        Channel<PermissionStreamItem>? ch;
        lock (_gate) _subscribers.TryRemove(id, out ch);
        ch?.Writer.TryComplete();
    }

    public void WithdrawForAgent(string agentId) {
        lock (_gate) {
            _withdrawnAgents.Add(agentId);
            _owed.RemoveAll(o => o.Scope.AgentId == agentId);
            foreach (var scope in _suspended.Keys.Where(s => s.AgentId == agentId).ToList()) _suspended.Remove(scope);
            foreach (var e in _pending.Values.Where(e => e.Dto.AgentId == agentId).ToList())
                SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone);
        }
    }

    /// A tool that has run was answered where the daemon cannot see, so its prompt is moot. The
    /// agent and the session are matched as well as the tool: every hosted agent posts on the same
    /// shared token, and a notice retires only what its own session registered. A prompt that
    /// carries a tool-use id is matched by it alone. One raised without an id (Claude's permission
    /// hook never sends one) is matched by tool name and input within the subagent scope the hook
    /// ran in, and only after the completions this scope is owed: a call allowed here runs and
    /// reports too, and hooks run asynchronously, so its report can land after an identical prompt
    /// nobody has answered was raised. A report nothing accounts for and no prompt matches is
    /// dropped; a false match would deny a live hook, so the fallback runs only while the ledger
    /// is whole. Among identical pending prompts the oldest goes first.
    public bool TryWithdrawTool(
            string agentId, string sessionId, string? toolUseId, string? toolName = null, JsonElement? toolInput = null, string? subagentId = null) {
        lock (_gate) {
            PurgeLapsedLocked();
            var own = _pending.Values.Where(e => e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId);
            var entry = toolUseId is null ? null : own.FirstOrDefault(e => e.Dto.ToolUseId == toolUseId);
            if (entry is null && toolName is not null && toolInput is { } input) {
                var scope = new Scope(agentId, sessionId, subagentId);
                var owed = _owed.FindIndex(o => o.Scope == scope && o.ToolName == toolName
                                             && o.ToolInput is { } owedInput && JsonElement.DeepEquals(owedInput, input));
                if (owed >= 0) {
                    _owed.RemoveAt(owed);
                    return false;
                }
                if (_suspended.ContainsKey(scope)) return false;
                entry = own
                    .Where(e => e.Dto.ToolUseId is null && e.SubagentId == subagentId && e.Dto.ToolName == toolName
                             && e.Dto.ToolInput is { } pendingInput && JsonElement.DeepEquals(pendingInput, input))
                    .OrderBy(e => RequestedAt(e.Dto))
                    .FirstOrDefault();
            }
            return entry is not null
                && SettleLocked(entry.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceToolSettled, isCompletionReport: true);
        }
    }

    /// A call allowed without a prompt (the launch policy's decision) still runs and reports, so
    /// the scope is owed its completion like an allowed prompt's.
    public void ExpectCompletion(string agentId, string sessionId, string? subagentId, string toolName, JsonElement? toolInput) {
        lock (_gate) OweLocked(new Owed(new Scope(agentId, sessionId, subagentId), toolName, toolInput));
    }

    /// Withdraws what a finished turn left pending in the session: the main agent's own requests
    /// for a null subagentId, otherwise that subagent's. A background subagent's prompt outlives
    /// the parent's turn, which is why the scope matters. Unlike an exit, a turn's end says nothing
    /// about the agent's next request. A prompt still pending here may have been allowed in the
    /// terminal with its report overtaken by Stop, so it is owed that report like any settlement;
    /// then the scope's owed completions, and its suspension, start their grace period.
    public int WithdrawTurn(string agentId, string sessionId, string? subagentId) {
        lock (_gate) {
            PurgeLapsedLocked();
            var scope   = new Scope(agentId, sessionId, subagentId);
            var settled = 0;
            foreach (var e in _pending.Values.Where(e =>
                         e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId && e.SubagentId == subagentId).ToList())
                if (SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceToolSettled)) settled++;
            var lapses = _time.GetUtcNow() + OwedGraceAfterTurn;
            foreach (var o in _owed) if (o.Scope == scope) o.LapsesAt ??= lapses;
            if (_suspended.TryGetValue(scope, out var suspendedUntil) && suspendedUntil is null) _suspended[scope] = lapses;
            return settled;
        }
    }

    public IReadOnlyList<PermissionPendingDto> PendingSnapshot() {
        lock (_gate) return _pending.Values.Select(e => e.Dto).ToList();
    }

    /// Only Claude's hooks report completions; another vendor's allow would never be collected.
    static bool ReportsCompletions(string vendor) => vendor == "claude";

    static DateTimeOffset RequestedAt(PermissionPendingDto dto) =>
        DateTimeOffset.TryParse(dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MaxValue;

    // Caller holds _gate. A gone agent reports nothing more and ends no more turns, so an entry
    // owed on its behalf could neither be collected nor lapse. A scope past the cap is one whose
    // reports are not arriving; forgetting any of them could let a late one pass for a terminal
    // answer, so the scope's fallback goes instead, and its ledger with it. Every completion
    // discarded while suspended re-arms the suspension: it lapses only a grace period after a
    // turn's end with nothing discarded since, because a call allowed under suspension reports on
    // its own clock, not the earlier turn's.
    void OweLocked(Owed owed) {
        if (_withdrawnAgents.Contains(owed.Scope.AgentId)) return;
        PurgeLapsedLocked();
        if (_suspended.ContainsKey(owed.Scope)) {
            _suspended[owed.Scope] = null;
            return;
        }
        if (_owed.Count(o => o.Scope == owed.Scope) >= MaxOwedPerScope) {
            _owed.RemoveAll(o => o.Scope == owed.Scope);
            _suspended[owed.Scope] = null;
            return;
        }
        _owed.Add(owed);
    }

    // Caller holds _gate.
    void PurgeLapsedLocked() {
        var now = _time.GetUtcNow();
        _owed.RemoveAll(o => o.LapsesAt is { } at && at <= now);
        foreach (var scope in _suspended.Where(kv => kv.Value is { } at && at <= now).Select(kv => kv.Key).ToList()) _suspended.Remove(scope);
    }

    // Caller holds _gate. Instance-scoped removal, the consent broker's discipline. Every
    // settlement but the completion report's own owes that report: the terminal can answer a
    // prompt the daemon still shows, so a deny or a withdrawal here does not prove the tool never
    // ran. An allowed call runs with the input the decision settled on, which is what it reports.
    bool SettleLocked(string requestId, PermissionDecision decision, string outcome, string source, bool isCompletionReport = false) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Entry>(requestId, entry))) return false;
        if (!isCompletionReport && ReportsCompletions(entry.Dto.Vendor))
            OweLocked(new Owed(new Scope(entry.Dto.AgentId, entry.Dto.SessionId, entry.SubagentId), entry.Dto.ToolName, decision.UpdatedInput ?? entry.Dto.ToolInput));
        Broadcast(new PermissionStreamItem.Resolved(new PermissionResolvedDto(requestId, outcome, source)));
        entry.Tcs.TrySetResult(new PermissionSettlement(decision, outcome, source));
        return true;
    }

    void Broadcast(PermissionStreamItem item) {
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(item);
    }
}
