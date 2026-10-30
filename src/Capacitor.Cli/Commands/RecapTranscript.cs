using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The markdown <c>kcap recap --full</c> and <c>--get-turn</c> print. A turn's trace is the only source
/// that carries every tool call and result; the <c>/recap</c> rows keep only prompts, replies and file
/// writes, so they stand in only where no closed turn covers the activity.
/// </summary>
static class RecapTranscript {
    /// <summary>One session's full transcript: its plans, then each closed turn's trace, then whatever
    /// happened after the last closed turn. <paramref name="fetchTurn"/> may throw
    /// <see cref="CapacitorApiException"/>; a turn that cannot be read is named, never skipped.</summary>
    internal static async Task<string> RenderSessionAsync(
            IReadOnlyList<RecapEntry> entries, string? turnsJson, Func<int, Task<TurnDetailResult>> fetchTurn) {
        var sb = new StringBuilder();

        foreach (var plan in entries.Where(e => e.Type == "plan")) {
            sb.AppendLine("## Plan");
            sb.AppendLine(plan.Content);
            sb.AppendLine();
        }

        var activity = entries.Where(e => e.Type != "plan").ToList();
        var turns    = ParseTurns(turnsJson);

        if (turns.Count == 0) {
            if (activity.Count > 0) {
                sb.AppendLine(RowsOnlyNote("No closed turn is recorded yet"));
                sb.AppendLine();
                sb.Append(RenderEntries(activity));
            }

            return sb.ToString();
        }

        foreach (var (index, _) in turns) {
            sb.AppendLine($"# Turn {index}");
            sb.AppendLine();
            sb.Append(await RenderTurnAsync(index, fetchTurn));
        }

        if (turns[^1].LastEventAt is { } closedAt && activity.Where(e => e.Timestamp > closedAt).ToList() is { Count: > 0 } open) {
            sb.AppendLine($"# After turn {turns[^1].Index}");
            sb.AppendLine();
            sb.AppendLine(RowsOnlyNote("This activity is not closed into a turn yet"));
            sb.AppendLine();
            sb.Append(RenderEntries(open));
        }

        return sb.ToString();
    }

    static string RowsOnlyNote(string why) =>
        $"({why}, so only prompts, replies and file writes are shown; tool calls and their results appear once the turn closes.)";

    static async Task<string> RenderTurnAsync(int index, Func<int, Task<TurnDetailResult>> fetchTurn) {
        TurnDetailResult result;

        try {
            result = await fetchTurn(index);
        } catch (CapacitorApiException ex) {
            return $"(Turn {index} could not be read: {ex.Message})" + Environment.NewLine + Environment.NewLine;
        }

        if (result is not TurnDetailResult.Found(var json))
            return $"(Turn {index} was not found on the server.)" + Environment.NewLine + Environment.NewLine;

        try {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.Arr("trace") is { } trace
                ? RenderTrace(trace)
                : $"(No trace is recorded for turn {index}.)" + Environment.NewLine + Environment.NewLine;
        } catch (JsonException) {
            return $"(Turn {index} came back unreadable.)" + Environment.NewLine + Environment.NewLine;
        }
    }

    /// <summary>Closed turns in index order, with when each ended. Empty for a missing or unreadable list.</summary>
    internal static List<(int Index, DateTimeOffset? LastEventAt)> ParseTurns(string? turnsJson) {
        if (string.IsNullOrWhiteSpace(turnsJson)) return [];

        try {
            using var doc = JsonDocument.Parse(turnsJson);

            if (!doc.RootElement.IsArray) return [];

            var turns = new List<(int, DateTimeOffset?)>();

            foreach (var turn in doc.RootElement.EnumerateArray()) {
                if (turn.Num("turn_index") is not { } index || index is < 0 or > int.MaxValue) continue;

                DateTimeOffset? lastEventAt = turn.Str("last_event_at") is { } ended && DateTimeOffset.TryParse(ended, out var at)
                    ? at
                    : null;

                turns.Add(((int)index, lastEventAt));
            }

            return [.. turns.OrderBy(t => t.Item1)];
        } catch (JsonException) {
            return [];
        }
    }

    /// <summary>
    /// One turn's trace. Subagent entries carry <c>agent_id</c> and are interleaved with the main
    /// session's by timestamp, so a header is written whenever the active agent changes. A truncated
    /// field and a result with no output are both said, so neither reads as a complete empty answer.
    /// </summary>
    internal static string RenderTrace(JsonElement trace) {
        var sb = new StringBuilder();

        string? currentAgentId = null;
        var     sawAgentHeader = false;

        foreach (var entry in trace.EnumerateArray()) {
            if (!entry.IsObject) continue;

            var entryAgentId = entry.Str("agent_id");

            if (entryAgentId != currentAgentId) {
                currentAgentId = entryAgentId;

                if (entryAgentId is { Length: > 0 }) {
                    sb.AppendLine(entry.Str("agent_type") is { Length: > 0 } agentType
                        ? $"### Subagent: {agentType} ({entryAgentId})"
                        : $"### Subagent: {entryAgentId}");
                    sb.AppendLine();
                    sawAgentHeader = true;
                } else if (sawAgentHeader) {
                    sb.AppendLine("### (main session)");
                    sb.AppendLine();
                }
            }

            var kind      = entry.Str("kind");
            var truncated = entry.Bool("truncated") == true;

            switch (kind) {
                case "user_message":
                    sb.AppendLine("## User");
                    sb.AppendLine(entry.Str("text") ?? "");
                    if (truncated) sb.AppendLine(TruncatedNote(entry, "text"));
                    sb.AppendLine();

                    break;

                case "assistant_message":
                    sb.AppendLine("## Assistant");
                    sb.AppendLine(entry.Str("text") ?? "");
                    if (truncated) sb.AppendLine(TruncatedNote(entry, "text"));
                    sb.AppendLine();

                    break;

                case "tool_invocation":
                    sb.AppendLine($"## Tool: {entry.Str("tool")}");

                    if (entry.Prop("arguments") is { } args && !args.IsNull) sb.AppendLine(args.ToString());

                    if (truncated) sb.AppendLine(TruncatedNote(entry, "input"));
                    sb.AppendLine();

                    break;

                case "tool_result":
                    sb.AppendLine(entry.Bool("is_error") == true ? "### Error" : "### Result");
                    sb.AppendLine(entry.Str("output") is { Length: > 0 } output ? output : "(no output recorded)");
                    if (truncated) sb.AppendLine(TruncatedNote(entry, "output"));
                    sb.AppendLine();

                    break;

                default:
                    // assistant_thinking, plan, or a kind added later that carries text.
                    if (!string.IsNullOrEmpty(kind) && entry.Str("text") is { Length: > 0 } text) {
                        sb.AppendLine($"## {kind}");
                        sb.AppendLine(text);
                        if (truncated) sb.AppendLine(TruncatedNote(entry, "text"));
                        sb.AppendLine();
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    static string TruncatedNote(JsonElement entry, string what) =>
        entry.Num("output_bytes") is { } bytes
            ? $"(The {what} was truncated; {bytes} bytes were recorded.)"
            : $"(The {what} was truncated.)";

    /// <summary>
    /// <c>/recap</c> rows: prompts, replies and file writes, with a header whenever the subagent
    /// changes. Used only for activity no closed turn covers.
    /// </summary>
    internal static string RenderEntries(IEnumerable<RecapEntry> entries) {
        var sb = new StringBuilder();

        string? currentAgentId = null;

        foreach (var entry in entries) {
            if (entry.AgentId is not null && entry.AgentId != currentAgentId) {
                currentAgentId = entry.AgentId;
                sb.AppendLine(entry.AgentType is not null ? $"### Agent ({entry.AgentType})" : $"### Agent {entry.AgentId}");
                sb.AppendLine();
            } else if (entry.AgentId is null) {
                currentAgentId = null;
            }

            switch (entry.Type) {
                case "plan":
                    sb.AppendLine("## Plan");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;

                case "user_prompt":
                    sb.AppendLine("## User Prompt");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;

                case "assistant_text":
                    sb.AppendLine("## Assistant");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;

                case "write":
                    var writePath = entry.FilePath ?? "unknown";
                    sb.AppendLine($"## Write {writePath}");
                    sb.AppendLine($"```{LanguageHint(writePath)}");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine("```");
                    sb.AppendLine();

                    break;

                case "edit":
                    sb.AppendLine($"## Edit {entry.FilePath ?? "unknown"}");
                    sb.AppendLine("```");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine("```");
                    sb.AppendLine();

                    break;
            }
        }

        return sb.ToString();
    }

    static string LanguageHint(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch {
            ".cs"         => "csharp",
            ".js"         => "javascript",
            ".ts"         => "typescript",
            ".tsx"        => "tsx",
            ".jsx"        => "jsx",
            ".py"         => "python",
            ".rb"         => "ruby",
            ".go"         => "go",
            ".rs"         => "rust",
            ".java"       => "java",
            ".kt"         => "kotlin",
            ".swift"      => "swift",
            ".md"         => "markdown",
            ".json"       => "json",
            ".yaml"       => "yaml",
            ".yml"        => "yaml",
            ".xml"        => "xml",
            ".html"       => "html",
            ".css"        => "css",
            ".scss"       => "scss",
            ".sql"        => "sql",
            ".sh"         => "bash",
            ".bash"       => "bash",
            ".zsh"        => "bash",
            ".razor"      => "razor",
            ".toml"       => "toml",
            ".dockerfile" => "dockerfile",
            _             => ""
        };
}
