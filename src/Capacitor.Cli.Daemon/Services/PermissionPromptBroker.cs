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
    sealed record Entry(PermissionPendingDto Dto, TaskCompletionSource<PermissionSettlement> Tcs, string? SubagentId);
    /// A completion notice a scope is still owed: the call was allowed and will report once it ran.
    sealed record Owed(string AgentId, string SessionId, string? SubagentId, string ToolName, JsonElement? ToolInput);

    const int MaxOwed = 256;

    readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    readonly List<Owed> _owed = [];
    readonly ConcurrentDictionary<Guid, Channel<PermissionStreamItem>> _subscribers = new();
    readonly HashSet<string> _withdrawnAgents = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

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
            _owed.RemoveAll(o => o.AgentId == agentId);
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
    /// reports too, and hooks run asynchronously, so its notice can land after an identical prompt
    /// nobody has answered was raised. Among identical pending prompts the oldest goes first.
    public bool TryWithdrawTool(
            string agentId, string sessionId, string? toolUseId, string? toolName = null, JsonElement? toolInput = null, string? subagentId = null) {
        lock (_gate) {
            var own = _pending.Values.Where(e => e.Dto.AgentId == agentId && e.Dto.SessionId == sessionId);
            var entry = toolUseId is null ? null : own.FirstOrDefault(e => e.Dto.ToolUseId == toolUseId);
            if (entry is null && toolName is not null && toolInput is { } input) {
                var owed = _owed.FindIndex(o => o.AgentId == agentId && o.SessionId == sessionId && o.SubagentId == subagentId
                                             && o.ToolName == toolName && o.ToolInput is { } owedInput && JsonElement.DeepEquals(owedInput, input));
                if (owed >= 0) {
                    _owed.RemoveAt(owed);
                    return false;
                }
                entry = own
                    .Where(e => e.Dto.ToolUseId is null && e.SubagentId == subagentId && e.Dto.ToolName == toolName
                             && e.Dto.ToolInput is { } pendingInput && JsonElement.DeepEquals(pendingInput, input))
                    .OrderBy(e => RequestedAt(e.Dto))
                    .FirstOrDefault();
            }
            return entry is not null
                && SettleLocked(entry.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceToolSettled);
        }
    }

    /// A call allowed without a prompt (the launch policy's decision) still runs and reports, so
    /// the scope is owed its completion like an allowed prompt's.
    public void ExpectCompletion(string agentId, string sessionId, string? subagentId, string toolName, JsonElement? toolInput) {
        lock (_gate) OweLocked(new Owed(agentId, sessionId, subagentId, toolName, toolInput));
    }

    static DateTimeOffset RequestedAt(PermissionPendingDto dto) =>
        DateTimeOffset.TryParse(dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MaxValue;

    // Caller holds _gate. Bounded because a call allowed and then never run (an interrupted turn)
    // reports nothing; the turn's end clears its scope's leftovers before that.
    void OweLocked(Owed owed) {
        if (_owed.Count >= MaxOwed) _owed.RemoveAt(0);
        _owed.Add(owed);
    }

    /// Withdraws what a finished turn left pending in the session: the main agent's own requests
    /// for a null subagentId, otherwise that subagent's. A background subagent's prompt outlives
    /// the parent's turn, which is why the scope matters. Unlike an exit, a turn's end says nothing
    /// about the agent's next request.
    public int WithdrawTurn(string agentId, string sessionId, string? subagentId) {
        lock (_gate) {
            _owed.RemoveAll(o => o.AgentId == agentId && o.SessionId == sessionId && o.SubagentId == subagentId);
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

    // Caller holds _gate. Instance-scoped removal, the consent broker's discipline. An allowed
    // call runs with the input the decision settled on, which is what its completion reports.
    bool SettleLocked(string requestId, PermissionDecision decision, string outcome, string source) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Entry>(requestId, entry))) return false;
        if (decision.Behavior == PermissionSettlements.Allow)
            OweLocked(new Owed(entry.Dto.AgentId, entry.Dto.SessionId, entry.SubagentId, entry.Dto.ToolName, decision.UpdatedInput ?? entry.Dto.ToolInput));
        Broadcast(new PermissionStreamItem.Resolved(new PermissionResolvedDto(requestId, outcome, source)));
        entry.Tcs.TrySetResult(new PermissionSettlement(decision, outcome, source));
        return true;
    }

    void Broadcast(PermissionStreamItem item) {
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(item);
    }
}
