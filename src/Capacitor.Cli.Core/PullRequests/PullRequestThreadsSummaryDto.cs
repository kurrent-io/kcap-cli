using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

/// How many review threads are still unresolved. Absent from a server that predates it.
public sealed record PullRequestThreadsSummaryDto {
    [JsonPropertyName("availability")]
    public required PullRequestAvailabilityDto Availability { get; init; }
    [JsonPropertyName("unresolved")]
    public PullRequestCountDto? Unresolved { get; init; }
}
