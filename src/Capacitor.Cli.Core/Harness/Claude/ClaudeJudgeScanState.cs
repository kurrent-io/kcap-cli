namespace Capacitor.Cli.Core.Harness.Claude;

using System.Text.Json.Serialization;

/// <summary>How far a session's transcript files have been read for the judge, and what they held
/// up to there, so the next hook reads only what was appended since.</summary>
sealed class ClaudeJudgeScanState {
    [JsonPropertyName("transcript")] public string Transcript { get; set; } = "";
    [JsonPropertyName("cursors")] public List<ClaudeJudgeScanCursor> Cursors { get; set; } = [];
    [JsonPropertyName("turns")] public List<ClaudeJudgeScanTurn> Turns { get; set; } = [];
    [JsonPropertyName("refusals")] public List<ClaudeJudgeScanRefusal> Refusals { get; set; } = [];

    /// <summary>Refusals dropped from the oldest end to bound the file, by the transcript they came
    /// from; any at all means the session's history is no longer whole.</summary>
    [JsonPropertyName("dropped_refusals")] public Dictionary<string, int> DroppedRefusals { get; set; } = [];
}

/// <param name="Offset">Bytes consumed, always at a line boundary.</param>
sealed record ClaudeJudgeScanCursor(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("offset")] long Offset);

sealed record ClaudeJudgeScanTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prompt_id")] string? PromptId);

/// <param name="ToolName">Null until the refused call's own line has been found.</param>
/// <param name="Target">Already clipped to what the wire accepts; <paramref name="Clipped"/> says
/// whether it was. Kept instead of the call's input, which for a refused write is the whole file.</param>
sealed record ClaudeJudgeScanRefusal(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("call_id")] string CallId,
    [property: JsonPropertyName("prompt_id")] string? PromptId,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("clipped")] bool Clipped);
