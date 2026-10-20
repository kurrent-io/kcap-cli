using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Claude;

/// Where Claude Code files a session's subagents: each one's transcript and meta, beside the parent's.
public static class ClaudeSubagentFiles {
    const string Prefix     = "agent-";
    const string MetaSuffix = ".meta.json";

    public static string DirectoryOf(string parentTranscriptPath) =>
        Path.Combine(Path.ChangeExtension(parentTranscriptPath, null), "subagents");

    public static string TranscriptPath(string parentTranscriptPath, string agentId) =>
        Path.Combine(DirectoryOf(parentTranscriptPath), $"{Prefix}{agentId}.jsonl");

    /// The subagents started so far and not in <paramref name="known"/>, each with the tool call that
    /// started it. The meta file is written at the start, so it names a foreground subagent while it
    /// runs; one still being written, or naming no call, is skipped until a later read.
    public static IReadOnlyList<(string ToolUseId, string AgentId)> Starts(string parentTranscriptPath, IReadOnlySet<string> known) {
        var found = new List<(string, string)>();
        try {
            foreach (var file in Directory.EnumerateFiles(DirectoryOf(parentTranscriptPath), Prefix + "*" + MetaSuffix)) {
                var name    = Path.GetFileName(file);
                var agentId = name[Prefix.Length..^MetaSuffix.Length];
                if (agentId.Length == 0 || known.Contains(agentId)) continue;
                if (ToolUseId(file) is { } toolUseId) found.Add((toolUseId, agentId));
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    static string? ToolUseId(string metaPath) {
        try {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(metaPath));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("toolUseId", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } value ? value : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }
}
