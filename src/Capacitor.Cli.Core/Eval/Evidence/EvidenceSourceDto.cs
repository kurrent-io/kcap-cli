using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceSourceDto {
    [JsonPropertyName("source_id")]        public required string  SourceId       { get; init; }
    [JsonPropertyName("kind")]             public required string  Kind           { get; init; }
    [JsonPropertyName("session_id")]       public required string  SessionId      { get; init; }
    [JsonPropertyName("agent_id")]         public          string? AgentId        { get; init; }
    [JsonPropertyName("agent_type")]       public          string? AgentType      { get; init; }
    [JsonPropertyName("parent_source_id")] public          string? ParentSourceId { get; init; }
    [JsonPropertyName("revision_cutoff")]  public          long    RevisionCutoff { get; init; }
    [JsonPropertyName("first_revision")]   public          long    FirstRevision  { get; init; }
    [JsonPropertyName("turn_count")]       public          int?    TurnCount      { get; init; }
    [JsonPropertyName("availability")]     public required string  Availability   { get; init; }
    [JsonIgnore] public bool IsAvailable => Availability == "available";
}
