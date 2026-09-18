using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeEvidenceCoverage</c> field for field. Null on a question
/// means retrieval was not observed for it (the tools path, or a degraded server-side discovery);
/// non-null is a strict account of what was consulted, cited and omitted.</summary>
public record EvalEvidenceCoverage {
    [JsonPropertyName("policy_version")] public required string PolicyVersion { get; init; }

    [JsonPropertyName("scope_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScopeVersion { get; init; }

    [JsonPropertyName("sources_consulted")]   public List<string> SourcesConsulted   { get; init; } = [];
    [JsonPropertyName("sources_unavailable")] public List<string> SourcesUnavailable { get; init; } = [];
    [JsonPropertyName("citations")]           public List<EvalEvidenceCitation> Citations { get; init; } = [];
    [JsonPropertyName("omissions")]           public List<EvalEvidenceOmission> Omissions { get; init; } = [];

    [JsonPropertyName("stop_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    [JsonIgnore]
    public bool IsComplete => Omissions.Count == 0 && SourcesUnavailable.Count == 0 && StopReason is null;
}
