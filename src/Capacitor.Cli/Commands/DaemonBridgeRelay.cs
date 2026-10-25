using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The notices a hook sends the daemon hosting its agent over the loopback bridge. Only a
/// daemon-spawned agent has both an id and a loopback bridge; for anything else every notice is a
/// no-op. Best effort on a short cap, and never past what the hook may still spend: the daemon is
/// local, and a wedged one must not push the hook's real work past the host's kill.
/// </summary>
internal static class DaemonBridgeRelay {
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The agent's turn ended (the user's move) or a new one began, so the daemon's own status
    /// surfaces show a PTY vendor's wait the way an ACP runtime's turn end already does.
    /// </summary>
    public static Task NotifyInputWaitAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, bool waiting, HookBudget budget) =>
        PostAsync(hosted, vendor, "input-wait", new JsonObject { ["session_id"] = sessionId, ["cwd"] = cwd, ["waiting"] = waiting }, budget);

    /// <summary>
    /// A subagent's own hook ran, so the daemon can count it live or, on its stop, drop it.
    /// <paramref name="sentAtMs"/> is the hook's own UTC clock in Unix milliseconds: hooks are
    /// asynchronous and the daemon runs their reports independently, so it orders a subagent's
    /// reports by this stamp rather than by arrival.
    /// </summary>
    public static Task NotifySubagentAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, string subagentId, bool live,
            long sentAtMs, HookBudget budget) =>
        PostAsync(hosted, vendor, "subagent", new JsonObject {
            ["session_id"]  = sessionId,
            ["cwd"]         = cwd,
            ["subagent_id"] = subagentId,
            ["live"]        = live,
            ["sent_at"]     = sentAtMs,
        }, budget);

    /// <summary>
    /// A tool ran, a subagent stopped (<paramref name="subagentId"/>) or, with neither id, the
    /// turn ended, so a prompt the daemon still holds for it can be retired: the answer was given
    /// in the vendor's own terminal, which the daemon cannot see. A finished tool may also name
    /// the subagent that ran it and carry its name and input, for a prompt held without its id.
    /// <paramref name="sentAtMs"/> is the hook's own clock: the daemon handles a notice off the
    /// hook's wait, so a late one must not reach a prompt registered after it was sent.
    /// </summary>
    public static Task NotifyToolSettledAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, string? toolUseId, string? subagentId, HookBudget budget,
            string? toolName = null, JsonNode? toolInput = null, long? sentAtMs = null) {
        var payload = new JsonObject { ["session_id"] = sessionId, ["cwd"] = cwd };
        if (toolUseId is not null) payload["tool_use_id"] = toolUseId;
        if (subagentId is not null) payload["subagent_id"] = subagentId;
        // A name or input past the prompt's own caps was never held, and would push the notice
        // past the bridge's body cap, losing the id match too.
        if (toolUseId is not null && toolName is not null && toolInput is not null
         && Encoding.UTF8.GetByteCount(toolName) <= PermissionWire.MaxToolNameBytes
         && Encoding.UTF8.GetByteCount(toolInput.ToJsonString()) <= PermissionWire.MaxElementBytes) {
            payload["tool_name"]  = toolName;
            payload["tool_input"] = toolInput;
        }
        if (sentAtMs is { } sent) payload["sent_at"] = sent;

        return PostAsync(hosted, vendor, "tool-settled", payload, budget);
    }

    static async Task PostAsync(HostedAgent hosted, string vendor, string route, JsonObject payload, HookBudget budget) {
        var remaining = budget.Remaining;
        var cap       = remaining < Cap ? remaining : Cap;
        if (cap <= TimeSpan.Zero) return;
        if (hosted.AgentId is not { } agentId) return;
        // A notice is not worth a line on stderr, so a refused bridge is as quiet as no bridge.
        if (hosted.Bridge is not DaemonBridge.Loopback bridge) return;

        payload["agent_id"] = agentId;

        try {
            // On the hook's own clock, so the cap is the hook's time and not the host's.
            using var deadline = new CancellationTokenSource(cap, budget.Time);
            using var client   = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var content  = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _        = await client.PostAsync($"{bridge.BaseUrl}/{vendor}/{route}", content, deadline.Token);
        } catch {
            // A notice, never a hook outcome.
        }
    }
}
