using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceTurnEntryDto {
    [JsonPropertyName("turn_ref")]       public required string  TurnRef       { get; init; }
    [JsonPropertyName("events_ref")]     public          string? EventsRef     { get; init; }
    [JsonPropertyName("range_state")]    public required string  RangeState    { get; init; }
    [JsonPropertyName("index")]          public          int     Index         { get; init; }
    [JsonPropertyName("start_revision")] public          long    StartRevision { get; init; }
    [JsonPropertyName("end_revision")]   public          long    EndRevision   { get; init; }
}
