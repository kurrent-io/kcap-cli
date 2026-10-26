using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

static class ClaudeSessionStartClaim {
    /// <summary>Records the live process before global orphan reaping can end its resumed session.
    /// Capture's own gates decide whether the watcher adds an origin for future end delivery.</summary>
    public static async Task<string> PrepareAsync(
            string body, ConfigRoot config, Func<int?> agentPid, Func<string, Task<bool>> shouldSuppress) {
        try {
            if (JsonNode.Parse(body) is not JsonObject hook
             || ClaudeHookCommand.ToKebab(Text(hook, "hook_event_name") ?? "") != "session-start"
             || SessionId.Parse(Text(hook, "session_id")) is not { } session
             || Text(hook, "transcript_path") is null
             || agentPid() is not { } pid)
                return body;

            // /clear reads this process's preceding session before the new claim replaces it.
            var stamped = PreviousSession.Stamp(hook, config, () => pid);
            if (!await shouldSuppress(session.Value))
                AgentSessions.OnThisMachine(config).Claim(pid, session);

            return stamped ? hook.ToJsonString() : body;
        } catch {
            return body;
        }
    }

    static string? Text(JsonObject hook, string key) =>
        hook[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
