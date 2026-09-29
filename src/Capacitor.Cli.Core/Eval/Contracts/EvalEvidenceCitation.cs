using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeEvidenceCitation</c> field for field.</summary>
public record EvalEvidenceCitation {
    [JsonPropertyName("ref")] public required string Ref { get; init; }

    [JsonPropertyName("digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Digest { get; init; }
}
