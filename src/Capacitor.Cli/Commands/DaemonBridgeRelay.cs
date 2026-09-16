using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

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
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, bool waiting, TimeSpan budget) =>
        PostAsync(hosted, vendor, "input-wait", new JsonObject { ["session_id"] = sessionId, ["cwd"] = cwd, ["waiting"] = waiting }, budget);

    /// <summary>
    /// A tool ran, a subagent stopped (<paramref name="subagentId"/>) or, with neither id, the
    /// turn ended, so a prompt the daemon still holds for it can be retired: the answer was given
    /// in the vendor's own terminal, which the daemon cannot see.
    /// </summary>
    public static Task NotifyToolSettledAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, string? toolUseId, string? subagentId, TimeSpan budget) {
        var payload = new JsonObject { ["session_id"] = sessionId, ["cwd"] = cwd };
        if (toolUseId is not null) payload["tool_use_id"] = toolUseId;
        if (subagentId is not null) payload["subagent_id"] = subagentId;

        return PostAsync(hosted, vendor, "tool-settled", payload, budget);
    }

    static async Task PostAsync(HostedAgent hosted, string vendor, string route, JsonObject payload, TimeSpan budget) {
        var cap = budget < Cap ? budget : Cap;
        if (cap <= TimeSpan.Zero) return;
        if (hosted.AgentId is not { } agentId) return;
        // A notice is not worth a line on stderr, so a refused bridge is as quiet as no bridge.
        if (hosted.Bridge is not DaemonBridge.Loopback bridge) return;

        payload["agent_id"] = agentId;

        try {
            using var client  = new HttpClient { Timeout = cap };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _       = await client.PostAsync($"{bridge.BaseUrl}/{vendor}/{route}", content);
        } catch {
            // A notice, never a hook outcome.
        }
    }
}
