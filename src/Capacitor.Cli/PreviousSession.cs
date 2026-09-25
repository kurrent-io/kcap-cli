using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// <c>/clear</c> gives the agent a new session id. This stamps <c>previous_session_id</c> with the
/// session the same agent process was running, so the server knows the new session followed it in
/// the same terminal. That is order, not shared work: the server files each session under work items
/// by its own evidence alone. Keyed on the process, not the folder, so two terminals in one folder
/// never link to each other. Must run before the start's own claim replaces the session it reads.
/// </summary>
static class PreviousSession {
    public static bool Stamp(JsonObject hook, ConfigRoot config, Func<int?> agentPid) {
        if (hook["previous_session_id"] is not null) return false;
        if (ClearedSession(hook, config, agentPid) is not { } cleared) return false;

        hook["previous_session_id"] = cleared.Value;

        return true;
    }

    static SessionId? ClearedSession(JsonObject hook, ConfigRoot config, Func<int?> agentPid) {
        if (!IsClearStart(hook)) return null;
        if (agentPid() is not { } pid) return null;

        var claimed = AgentSessions.OnThisMachine(config).Of(pid);

        return claimed == SessionId.Parse(TryGetString(hook, "session_id")) ? null : claimed;
    }

    static bool IsClearStart(JsonObject hook) =>
        (TryGetString(hook, "hook_event_name"), TryGetString(hook, "source")) is ("SessionStart", "clear");

    static string? TryGetString(JsonObject hook, string key) =>
        hook[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
