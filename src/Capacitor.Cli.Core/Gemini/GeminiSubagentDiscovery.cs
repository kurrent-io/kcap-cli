using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core; // JsonElement Str/Obj/Arr extensions

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
        } catch { /* unreadable / malformed → no subagents */ }

        return null;
    }

    /// <summary>
    /// Nested subagent <c>*.jsonl</c> files recorded alongside a parent main transcript
    /// (empty when the session spawned none). Path: <c>chats/&lt;dashedParent&gt;/*.jsonl</c>.
    /// Single-level — shared by the live watcher/teardown paths, which stay single-level for
    /// now (AI-1383 D3 scoped the recursive fix to import only; see
    /// <see cref="EnumerateDescendantFiles"/>).
    /// </summary>
    public static IReadOnlyList<string> EnumerateSubagentFiles(string transcriptPath) {
        if (ReadParentSessionId(transcriptPath) is not { } dashedParent) return [];
        if (Path.GetDirectoryName(transcriptPath) is not { } chatsDir) return [];

        var subDir = GeminiPaths.SubagentDir(chatsDir, dashedParent);
        if (!Directory.Exists(subDir)) return [];

        return Directory.EnumerateFiles(subDir, "*.jsonl").ToList();
    }

    /// <summary>Import-side recursion depth cap (AI-1383 D3) — a descendant beyond this depth
    /// is neither discovered nor recursed into; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// One recursively-discovered descendant subagent file. <c>ImmediateParentTranscriptPath</c>
    /// is THIS descendant's own immediate parent's transcript — NOT the root's — because a
    /// grandchild's <c>agent_name</c>/invocation-type mapping lives in its immediate parent's
    /// <c>invoke_agent</c> tool call, not the root's (AI-1383 D3). <c>DashedId</c> is the
    /// descendant's own dashed session id (the filename stem), and <c>Depth</c> is 1 for a
    /// direct child of the root, 2 for a grandchild, etc.
    /// </summary>
    public readonly record struct DescendantFile(
        string File,
        string ImmediateParentTranscriptPath,
        string DashedId,
        int    Depth);

    /// <summary>Recursive descendant discovery result: the discovered files plus a count of
    /// descendants beyond <see cref="MaxDescendantDepth"/> that were deliberately NOT imported
    /// (never silent — the caller surfaces this in the import summary).</summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantFile> Files,
        int                           DescendantsOmitted);

    /// <summary>
    /// Recursively discovers every descendant subagent transcript under
    /// <paramref name="rootTranscriptPath"/>: the root's own <c>chats/&lt;root&gt;/*.jsonl</c>,
    /// then — for each discovered subagent — that subagent's OWN <c>chats/&lt;sub&gt;/*.jsonl</c>,
    /// and so on. Child directories are always derived from the ROOT's <c>chats/</c> directory,
    /// never relative to a nested dir. Deterministic BFS (files ordered by filename at each
    /// level); a visited-id set guards against a reachable cycle (it simply stops re-descending,
    /// terminating the walk). Depth &gt; <see cref="MaxDescendantDepth"/> is neither imported nor
    /// promoted — counted in <see cref="DescendantDiscoveryResult.DescendantsOmitted"/> instead
    /// (AI-1383 D3). Import-only for now (see <see cref="EnumerateSubagentFiles"/>).
    /// </summary>
    public static DescendantDiscoveryResult EnumerateDescendantFiles(string rootTranscriptPath) {
        if (ReadParentSessionId(rootTranscriptPath) is not { } rootDashedId) return new([], 0);
        if (Path.GetDirectoryName(rootTranscriptPath) is not { } chatsDir) return new([], 0);

        var files   = new List<DescendantFile>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootDashedId };
        var omitted = 0;

        var frontier = new Queue<(string TranscriptPath, string DashedId, int Depth)>();
        frontier.Enqueue((rootTranscriptPath, rootDashedId, 0));

        while (frontier.Count > 0) {
            var (transcriptPath, dashedId, depth) = frontier.Dequeue();

            var subDir = GeminiPaths.SubagentDir(chatsDir, dashedId);
            if (!Directory.Exists(subDir)) continue;

            IEnumerable<string> children;
            try {
                children = Directory.EnumerateFiles(subDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
            } catch {
                continue; // hostile/inaccessible nested dir must not abort the whole walk
            }

            foreach (var file in children) {
                var subId = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(subId, out _)) continue; // only well-formed <subId>.jsonl
                if (!visited.Add(subId)) continue;           // cycle guard — already reached

                var childDepth = depth + 1;
                if (childDepth > MaxDescendantDepth) { omitted++; continue; }

                files.Add(new DescendantFile(file, transcriptPath, subId, childDepth));
                frontier.Enqueue((file, subId, childDepth));
            }
        }

        return new DescendantDiscoveryResult(files, omitted);
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
        } catch { /* best effort */ }

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
