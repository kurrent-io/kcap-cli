using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

public sealed record SessionModelTokensDto {
    [JsonPropertyName("model")]              public string? Model            { get; init; }
    [JsonPropertyName("input_tokens")]       public long    InputTokens      { get; init; }
    [JsonPropertyName("output_tokens")]      public long    OutputTokens     { get; init; }
    [JsonPropertyName("cache_read_tokens")]  public long    CacheReadTokens  { get; init; }
    [JsonPropertyName("cache_write_tokens")] public long    CacheWriteTokens { get; init; }

    public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}
