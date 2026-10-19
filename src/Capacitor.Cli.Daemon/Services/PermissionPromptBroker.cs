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
internal sealed class PermissionPromptBroker {
    sealed record Entry(PermissionPendingDto Dto, TaskCompletionSource<PermissionSettlement> Tcs, string? SubagentId, long Seq);
    sealed record AllowedCall(PermissionPendingDto Dto, string? SubagentId, JsonElement? Input) {
        public string AgentId => Dto.AgentId;
        public string SessionId => Dto.SessionId;
        public string? ToolUseId => Dto.ToolUseId;
        public string ToolName => Dto.ToolName;
    }

    readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Guid, Channel<PermissionStreamItem>> _subscribers = new();
    readonly HashSet<string> _withdrawnAgents = new(StringComparer.Ordinal);
    // A Claude prompt allowed before its call finished: that call's notice is its own, and must not
    // retire an identical prompt still waiting. Kept until the notice or the turn's end, and only
    // for Claude, whose hook sends both. A turn that outgrows the bound stops matching by name and
    // input until it ends, rather than forgetting what it owes.
    readonly List<AllowedCall> _allowedCalls = [];
    readonly HashSet<(string AgentId, string SessionId, string? SubagentId)> _overflowed = [];
    const int MaxAllowedCallsPerTurn = 64;
    readonly object _gate = new();
    long _seq;

    public bool HasSubscriber => !_subscribers.IsEmpty;

    /// <param name="subagentId">The vendor's id for the subagent whose tool call this is, null for
    /// the main agent's own. Kept off the wire: only <see cref="WithdrawTurn"/> reads it.</param>
    public Task<PermissionSettlement> Register(PermissionPendingDto dto, string? subagentId = null) {
        lock (_gate) {
            if (_withdrawnAgents.Contains(dto.AgentId))
                return Task.FromResult(new PermissionSettlement(
                    PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone));

            // Completed while the gate is held: a continuation running inline would re-enter it.
            var entry = new Entry(dto, new(TaskCreationOptions.RunContinuationsAsynchronously), subagentId, ++_seq);
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
            _allowedCalls.RemoveAll(c => c.AgentId == agentId);
            _overflowed.RemoveWhere(s => s.AgentId == agentId);
            foreach (var e in _pending.Values.Where(e => e.Dto.AgentId == agentId).ToList())
                SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone);
        }
    }

    /// A tool that has run was answered where the daemon cannot see, so its prompt is moot. The
    /// agent and the session are matched as well as the id: every hosted agent posts on the same
    /// shared token, and a notice retires only what its own session registered. A prompt registered
    /// without a tool id (Claude's hook sends none) matches the same subagent's call by tool name and
    /// input instead, the oldest first, so one finished call retires one prompt. A call that an
    /// allowed prompt is still owed is that prompt's, and retires nothing.
    /// <param name="sentAt">When the hook sent the notice: a prompt registered after it, pending or
    /// owed, is a later call's, whatever its name and input.</param>
    public bool TryWithdrawTool(string agentId, string sessionId, string toolUseId,
            string? subagentId = null, string? toolName = null, JsonElement? toolInput = null, DateTimeOffset? sentAt = null) {
        lock (_gate) {
            var entry = _pending.Values.FirstOrDefault(e =>
                e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId && e.Dto.ToolUseId == toolUseId);
            if (entry is null && toolName is not null && toolInput is { } input) {
                var owed = _allowedCalls.FindIndex(c => c.AgentId == agentId && c.SessionId == sessionId
                    && (sentAt is null || RequestedBy(c.Dto, sentAt.Value))
                    && (c.ToolUseId == toolUseId
                     || c.ToolUseId is null && c.SubagentId == subagentId && c.ToolName == toolName
                        && c.Input is { } ran && JsonElement.DeepEquals(ran, input)));
                if (owed >= 0) {
                    _allowedCalls.RemoveAt(owed);
                    return false;
                }
                if (_overflowed.Contains((agentId, sessionId, subagentId))) return false;
                entry = _pending.Values
                    .Where(e => e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId && e.Dto.ToolUseId is null
                             && e.SubagentId == subagentId && e.Dto.ToolName == toolName
                             && (sentAt is null || RequestedBy(e.Dto, sentAt.Value))
                             && e.Dto.ToolInput is { } held && JsonElement.DeepEquals(held, input))
                    .MinBy(e => e.Seq);
            }
            return entry is not null
                && SettleLocked(entry.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceToolSettled);
        }
    }

    /// Withdraws what a finished turn left pending in the session: the main agent's own requests
    /// for a null subagentId, otherwise that subagent's. A background subagent's prompt outlives
    /// the parent's turn, which is why the scope matters. Unlike an exit, a turn's end says nothing
    /// about the agent's next request.
    public int WithdrawTurn(string agentId, string sessionId, string? subagentId) {
        lock (_gate) {
            _allowedCalls.RemoveAll(c => c.AgentId == agentId && c.SessionId == sessionId && c.SubagentId == subagentId);
            _overflowed.Remove((agentId, sessionId, subagentId));
            var settled = 0;
            foreach (var e in _pending.Values.Where(e =>
                         e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId && e.SubagentId == subagentId).ToList())
                if (SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceToolSettled)) settled++;
            return settled;
        }
    }

    public IReadOnlyList<PermissionPendingDto> PendingSnapshot() {
        lock (_gate) return _pending.Values.Select(e => e.Dto).ToList();
    }

    // Caller holds _gate. Instance-scoped removal, the consent broker's discipline.
    bool SettleLocked(string requestId, PermissionDecision decision, string outcome, string source) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Entry>(requestId, entry))) return false;
        // The call runs with the input the answer gave it, when it gave one.
        if (decision.Behavior == PermissionSettlements.Allow) OweLocked(entry.Dto, entry.SubagentId, decision.UpdatedInput ?? entry.Dto.ToolInput);
        Broadcast(new PermissionStreamItem.Resolved(new PermissionResolvedDto(requestId, outcome, source)));
        entry.Tcs.TrySetResult(new PermissionSettlement(decision, outcome, source));
        return true;
    }

    /// A call allowed without a prompt (by the agent's policy) is owed its notice just the same, so
    /// the notice does not retire an identical prompt still waiting for the user.
    public void OweCall(PermissionPendingDto dto, string? subagentId) {
        lock (_gate) OweLocked(dto, subagentId, dto.ToolInput);
    }

    // Caller holds _gate.
    void OweLocked(PermissionPendingDto dto, string? subagentId, JsonElement? input) {
        if (dto.Vendor != "claude") return;
        var turn = (dto.AgentId, dto.SessionId, subagentId);
        if (_allowedCalls.Count(c => (c.AgentId, c.SessionId, c.SubagentId) == turn) >= MaxAllowedCallsPerTurn) _overflowed.Add(turn);
        else _allowedCalls.Add(new AllowedCall(dto, subagentId, input));
    }

    // An unparsed stamp is no ordering, so such a prompt is left to the turn's end.
    static bool RequestedBy(PermissionPendingDto dto, DateTimeOffset sentAt) =>
        DateTimeOffset.TryParse(dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at <= sentAt;

    void Broadcast(PermissionStreamItem item) {
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(item);
    }
}
