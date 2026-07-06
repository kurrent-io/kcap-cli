using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Gemini;

/// <summary>
/// Discovery + lifecycle-payload glue for Gemini's nested subagent transcripts (AI-900).
/// Gemini records each subagent in <c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>;
/// the parent's <c>invoke_agent</c> tool call persists <c>agentId == subId</c> plus
/// <c>args.agent_name</c>. Shared by the historical import path (<c>GeminiImportSource</c>)
/// and the live watcher (<c>WatchCommand</c> / <c>GeminiHookCommand</c>) so both speak one
/// wire contract.
/// </summary>
public static class GeminiSubagentDiscovery {
    /// <summary>Dashed parent sessionId from a main transcript's header (first line), or null.</summary>
    public static string? ReadParentSessionId(string transcriptPath) {
        try {
            foreach (var line in File.ReadLines(transcriptPath)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);

                return doc.RootElement.Str("sessionId");
            }
        } catch {
            /* unreadable / malformed → no subagents */
        }

        return null;
    }

    /// <summary>
    /// Nested subagent <c>*.jsonl</c> files recorded alongside a parent main transcript
    /// (empty when the session spawned none). Path: <c>chats/&lt;dashedParent&gt;/*.jsonl</c>.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSubagentFiles(string transcriptPath) {
        if (ReadParentSessionId(transcriptPath) is not { } dashedParent || Path.GetDirectoryName(transcriptPath) is not { } chatsDir) return [];

        var subDir = GeminiPaths.SubagentDir(chatsDir, dashedParent);

        return !Directory.Exists(subDir) ? [] : Directory.EnumerateFiles(subDir, "*.jsonl").ToList();
    }

    /// <summary>
    /// Maps each subagent id (DASHED, the nested filename stem) to the <c>agent_name</c>
    /// from the parent's <c>invoke_agent</c> tool call — the subagent's only on-disk type
    /// signal. Best-effort; unmatched ids are absent (callers fall back to "subagent").
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveAgentTypes(string parentTranscriptPath) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try {
            foreach (var line in File.ReadLines(parentTranscriptPath)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.Str("type") != "gemini" || root.Arr("toolCalls") is not { } tcs) continue;

                foreach (var tc in tcs.EnumerateArray()) {
                    if (tc.Str("name") != "invoke_agent" || tc.Str("agentId") is not { } aid) continue;

                    if (tc.Obj("args") is { } args && args.Str("agent_name") is { Length: > 0 } name)
                        map[aid] = name;
                }
            }
        } catch {
            /* best effort */
        }

        return map;
    }

    /// <summary>
    /// Canonical (dashless) agent id from a subagent filename stem (a dashed UUID). Matches
    /// the dashless form the server routes/correlates on and canonicalizes defensively.
    /// </summary>
    public static string CanonicalAgentId(string dashedSubId) => dashedSubId.Replace("-", "");

    // ── Hook payloads (the vendor-agnostic /hooks/subagent-start|stop wire shape) ──

    /// <summary>
    /// <c>/hooks/subagent-start</c> body. cwd is "" — Gemini carries no cwd for subagents
    /// (same as the parent session); the server's HookBase requires the (non-null) field.
    /// </summary>
    public static JsonObject BuildStartPayload(string parentSessionId, string agentId, string agentType, string subagentTranscriptPath) =>
        new() {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = agentType,
            ["transcript_path"] = subagentTranscriptPath,
            ["cwd"]             = "",
        };

    /// <summary><c>/hooks/subagent-stop</c> body.</summary>
    public static JsonObject BuildStopPayload(string parentSessionId, string agentId, string agentType, string subagentTranscriptPath) =>
        new() {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = agentType,
            ["transcript_path"]        = subagentTranscriptPath,
            ["cwd"]                    = "",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = subagentTranscriptPath,
            ["last_assistant_message"] = "",
        };
}
