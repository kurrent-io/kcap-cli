using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>A write we have authorised and are about to perform, or performed without yet recording
/// the outcome. The identity travels with it because an operation prepared under one account has to
/// be resolved before the next one retires anything, and the intended hash is what decides whether
/// its bytes landed — never evidence of ownership, of a relocation, or of a licence to
/// delete.</summary>
public sealed record PreparedSkillWrite {
    [JsonPropertyName("operation")] public required Guid           Operation { get; init; }
    [JsonPropertyName("intended")]  public required SkillReceipt   Intended  { get; init; }
    [JsonPropertyName("identity")]  public required SkillsIdentity Identity  { get; init; }
}
