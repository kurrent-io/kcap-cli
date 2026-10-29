using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Which harnesses a skill doc is delivered to, as the server sends it. A null axis
/// restricts nothing; an empty one matches nothing. Carried through to the manifest as provenance —
/// placement is what actually restricts a materialized file, since a tree several harnesses read
/// cannot enforce a vendor.</summary>
public sealed record SkillApplicability {
    [JsonPropertyName("vendors")]       public string[]? Vendors      { get; init; }
    [JsonPropertyName("platforms")]     public string[]? Platforms    { get; init; }
    [JsonPropertyName("session_kinds")] public string[]? SessionKinds { get; init; }
    [JsonPropertyName("flow_roles")]    public string[]? FlowRoles    { get; init; }
}
