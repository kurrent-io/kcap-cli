using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeObligationResult</c> field for field: one reconciled obligation with its certified
/// anchor and the certified evidence for its status, which never repeats the anchor.</summary>
public record EvalObligationResult {
    [JsonPropertyName("id")]     public required string               Id     { get; init; }
    [JsonPropertyName("title")]  public required string               Title  { get; init; }
    [JsonPropertyName("origin")] public required string               Origin { get; init; }
    [JsonPropertyName("status")] public required string               Status { get; init; }
    [JsonPropertyName("anchor")] public required EvalEvidenceCitation Anchor { get; init; }

    [JsonPropertyName("citations")] public List<EvalEvidenceCitation> Citations { get; init; } = [];

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}
