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

    public const int MaxCitations = 200;
    public const int MaxOmissions = 200;
    public const int MaxSources   = 2_000;
    public const int MaxDetail    = 500;

    /// <summary>The server's coverage rules; <c>coverage-v1</c> admits only its own vocabulary, anything else the full one.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 64) return "policy_version must be non-empty and at most 64 characters";
        var v1    = PolicyVersion == EvalService.CoveragePolicyVersion;
        var kinds = v1 ? EvalOmissionKinds.V1 : EvalOmissionKinds.All;
        var stops = v1 ? EvalStopReasons.V1 : EvalStopReasons.All;
        if (StopReason is not null && !stops.Contains(StopReason)) return $"stop_reason '{StopReason}' is unknown";
        if (SourcesConsulted.Count > MaxSources || SourcesUnavailable.Count > MaxSources) return $"a source list exceeds {MaxSources}";
        if (Citations.Count > MaxCitations) return $"citations exceeds {MaxCitations}";
        if (Omissions.Count > MaxOmissions) return $"omissions exceeds {MaxOmissions}";
        foreach (var s in SourcesConsulted.Concat(SourcesUnavailable))
            if (!Evidence.EvidenceRefText.TryDecodeSource(s, out _)) return $"'{s}' is not a source id";
        for (var i = 0; i < Citations.Count; i++) {
            var c = Citations[i];
            if (!Evidence.EvidenceRefText.TryParse(c.Ref, out _))                                        return $"citations[{i}].ref is not an evidence ref";
            if (c.Digest is not null && !(c.Digest.Length == 64 && c.Digest.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))) return $"citations[{i}].digest must be 64 lower-case hex characters";
        }
        for (var i = 0; i < Omissions.Count; i++) {
            var o = Omissions[i];
            if (!kinds.Contains(o.Kind))                                         return $"omissions[{i}].kind '{o.Kind}' is unknown";
            if (o.Count is < 1)                                                   return $"omissions[{i}].count must be at least 1";
            if (o.Ref is not null && !Evidence.EvidenceRefText.TryParse(o.Ref, out _)) return $"omissions[{i}].ref is not an evidence ref";
            if (o.Detail is { Length: > MaxDetail })                             return $"omissions[{i}].detail exceeds {MaxDetail} characters";
        }
        return null;
    }
}
