using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// Grok fires no hook for a subagent. It runs as a session of its own beside its parent's, and the
/// parent's <c>updates.jsonl</c> is the only record of it starting and finishing.
/// </summary>
public static class GrokSubagents {
    public static IEnumerable<GrokSubagentEvent> Read(IEnumerable<string> parentLines) {
        foreach (var line in parentLines) {
            if (!line.Contains("subagent_", StringComparison.Ordinal)) continue;

            GrokSubagentEvent? found = null;
            try {
                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.Str("method") == "_x.ai/session/update"
                 && root.Obj("params")?.Obj("update") is { } update
                 // The id names a sibling directory, so a non-GUID one could point outside the sessions dir.
                 && update.Str("child_session_id") is { Length: > 0 } child
                 && Guid.TryParse(child, out _)) {
                    found = update.Str("sessionUpdate") switch {
                        "subagent_spawned"  => new(child, update.Str("subagent_type"), Finished: false),
                        "subagent_finished" => new(child, update.Str("subagent_type"), Finished: true),
                        _                   => null
                    };
                }
            } catch (JsonException) { }

            if (found is not null) yield return found;
        }
    }

    public static string ChildTranscript(string parentTranscript, string childSessionId) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(parentTranscript)!)!, childSessionId, "updates.jsonl");

    public static string AgentId(string childSessionId) => childSessionId.Replace("-", "");
}
