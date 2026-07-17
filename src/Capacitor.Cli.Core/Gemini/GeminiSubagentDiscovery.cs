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
    /// is neither imported nor promoted; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Hard ceiling on the total number of nodes visited by the below-cap COUNTING walk
    /// (AI-1383 D3 review fix #2/#3) — mirrors <c>OpenCodeDb.MaxCountingNodes</c> for symmetry.
    /// The visited-id set already prevents an infinite loop on a reachable cycle; this bounds
    /// the total work a pathologically huge or wide capped subtree could otherwise force onto a
    /// single import pass. A real Gemini subagent tree tops out at a handful of nodes, so this
    /// is a generous safety valve, not a realistic limit.
    /// </summary>
    public const int MaxCountingNodes = 10_000;

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

    /// <summary>Recursive descendant discovery result. <see cref="Files"/> holds every
    /// descendant WITHIN <see cref="MaxDescendantDepth"/> — the import set.
    /// <see cref="DescendantsOmitted"/> is the ACCURATE total count of every descendant BELOW
    /// the cap, not merely the boundary children immediately past it — a chain through depths 9
    /// and 10 reports 2, not 1 (AI-1383 D3 review fix #3). <see cref="OmittedDescendantIds"/>
    /// carries those same omitted (dashed) ids, sorted, order-independent (AI-1383 D3 review fix
    /// #2). Never silent — the caller surfaces the count in the import summary.</summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantFile> Files,
        int                           DescendantsOmitted,
        IReadOnlyList<string>         OmittedDescendantIds);

    /// <summary>
    /// Recursively discovers every descendant subagent transcript under
    /// <paramref name="rootTranscriptPath"/>: the root's own <c>chats/&lt;root&gt;/*.jsonl</c>,
    /// then — for each discovered subagent — that subagent's OWN <c>chats/&lt;sub&gt;/*.jsonl</c>,
    /// and so on. Child directories are always derived from the ROOT's <c>chats/</c> directory,
    /// never relative to a nested dir. Deterministic BFS (files ordered by filename at each
    /// level); a visited-id set guards against a reachable cycle (it simply stops re-descending,
    /// terminating the walk). Depth &gt; <see cref="MaxDescendantDepth"/> is never imported, but
    /// the walk still recurses into it (same visited-set guard, bounded by
    /// <see cref="MaxCountingNodes"/>) purely to COUNT the whole omitted subtree accurately and
    /// surface its identities (AI-1383 D3 review fix #2/#3) —
    /// <see cref="DescendantDiscoveryResult.DescendantsOmitted"/> and
    /// <see cref="DescendantDiscoveryResult.OmittedDescendantIds"/>. Import-only for now (see
    /// <see cref="EnumerateSubagentFiles"/>).
    /// </summary>
    public static DescendantDiscoveryResult EnumerateDescendantFiles(string rootTranscriptPath) {
        if (ReadParentSessionId(rootTranscriptPath) is not { } rootDashedId) return new([], 0, []);
        if (Path.GetDirectoryName(rootTranscriptPath) is not { } chatsDir) return new([], 0, []);

        var files      = new List<DescendantFile>();
        var visited    = new HashSet<string>(StringComparer.Ordinal) { rootDashedId };
        var omittedIds = new List<string>();

        var frontier = new Queue<(string TranscriptPath, string DashedId, int Depth)>();
        frontier.Enqueue((rootTranscriptPath, rootDashedId, 0));

        while (frontier.Count > 0) {
            if (visited.Count >= MaxCountingNodes) break; // hard ceiling — see MaxCountingNodes
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
                if (visited.Count >= MaxCountingNodes) break;
                var subId = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(subId, out _)) continue; // only well-formed <subId>.jsonl
                if (!visited.Add(subId)) continue;           // cycle guard — already reached

                var childDepth = depth + 1;
                if (childDepth > MaxDescendantDepth) {
                    // Below the import cap: never imported, but keep walking (same visited-set
                    // guard) so the count reflects the WHOLE omitted subtree, not just its
                    // boundary (AI-1383 D3 review fix #2/#3).
                    omittedIds.Add(subId);
                    frontier.Enqueue((file, subId, childDepth));
                    continue;
                }

                files.Add(new DescendantFile(file, transcriptPath, subId, childDepth));
                frontier.Enqueue((file, subId, childDepth));
            }
        }

        omittedIds.Sort(StringComparer.Ordinal);
        return new DescendantDiscoveryResult(files, omittedIds.Count, omittedIds);
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
