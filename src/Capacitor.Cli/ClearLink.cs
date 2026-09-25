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
static class ClearLink {
    public static bool Link(JsonObject hook, ConfigRoot config, Func<int?> agentPid) {
        if (Text(hook, "hook_event_name") != "SessionStart" || Text(hook, "source") != "clear" || hook["previous_session_id"] is not null)
            return false;

        if (agentPid() is not { } pid
         || AgentSessions.OnThisMachine(config).Of(pid) is not { } previous
         || previous == SessionId.Parse(Text(hook, "session_id")))
            return false;

        hook["previous_session_id"] = previous.Value;

        return true;
    }

    static string? Text(JsonObject hook, string key) =>
        hook[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
