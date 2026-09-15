using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// One declared task. `Status` is pending|in_progress|completed|skipped and `Source` is `mcp`,
/// `user` or `adapter:&lt;name&gt;`; both are displayed, never branched on, so the server may widen them.
/// `StatusPartial` marks a status whose latest change came from a session the viewer cannot see.
public sealed record PlanLedgerTaskDto {
    [JsonPropertyName("task_id")]        public string? TaskId        { get; init; }
    [JsonPropertyName("ordinal")]        public int     Ordinal       { get; init; }
    [JsonPropertyName("title")]          public string  Title         { get; init; } = "";
    [JsonPropertyName("status")]         public string  Status        { get; init; } = "";
    [JsonPropertyName("note")]           public string? Note          { get; init; }
    [JsonPropertyName("source")]         public string  Source        { get; init; } = "";
    [JsonPropertyName("status_partial")] public bool    StatusPartial { get; init; }
}
