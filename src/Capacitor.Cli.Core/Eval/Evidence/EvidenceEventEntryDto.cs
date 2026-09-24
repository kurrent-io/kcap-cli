using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceEventEntryDto {
    [JsonPropertyName("ref")]          public required string                     Ref         { get; init; }
    [JsonPropertyName("revision")]     public          long                       Revision    { get; init; }
    [JsonPropertyName("event_type")]   public required string                     EventType   { get; init; }
    [JsonPropertyName("kind")]         public required string                     Kind        { get; init; }
    [JsonPropertyName("text")]         public          string?                    Text        { get; init; }
    [JsonPropertyName("text_body")]    public          EvidenceBodyDescriptorDto? TextBody    { get; init; }
    [JsonPropertyName("calls")]        public          List<EvidenceCallEntryDto>? Calls      { get; init; }
    [JsonPropertyName("calls_total")]  public          int?                       CallsTotal  { get; init; }
    [JsonPropertyName("output")]       public          string?                    Output      { get; init; }
    [JsonPropertyName("output_body")]  public          EvidenceBodyDescriptorDto? OutputBody  { get; init; }
    [JsonPropertyName("payload_body")] public required EvidenceBodyDescriptorDto  PayloadBody { get; init; }
}
