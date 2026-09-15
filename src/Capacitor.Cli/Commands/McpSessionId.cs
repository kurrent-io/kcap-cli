using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>The session an MCP tool call acts on, shared by every kcap MCP server whose tools take
/// an optional <c>session_id</c>. An explicit argument wins; otherwise the running harness's own
/// session (<see cref="HarnessRequesterContext"/>), never a bare inherited env var: a Claude Code
/// MCP server never sees <c>KCAP_SESSION_ID</c>, and one launched from another session's shell
/// inherits the parent's. Throws rather than sending a blank id, so the tool answers with a clean
/// error. Both sources canonicalize alike (a GUID to its 32-hex form).</summary>
static class McpSessionId {
    internal const string NoSessionIdMessage =
        "No session id: pass session_id explicitly or run inside a harness session kcap can identify (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).";

    internal static string Resolve(JsonObject? args) => Resolve(args, Environment.GetEnvironmentVariable);

    internal static string Resolve(JsonObject? args, Func<string, string?> getEnv) {
        if (args?["session_id"] is { } node) {
            // Shape-tested like RequireString: a number or object here must answer as a field error,
            // not fall out of the dispatcher as a generic internal failure.
            if (node is not JsonValue value || !value.TryGetValue<string>(out var explicitId))
                throw new ArgumentException("'session_id' must be a string.");
            if (explicitId.Length > 0)
                return WorkContextIds.CanonicalSessionId(explicitId) ?? throw new ArgumentException(NoSessionIdMessage);
        }

        var ambient = HarnessRequesterContext.Resolve(getEnv, Directory.Exists).SessionId;
        if (WorkContextIds.CanonicalSessionId(ambient) is { } fromEnv) return fromEnv;

        throw new ArgumentException(NoSessionIdMessage);
    }
}
