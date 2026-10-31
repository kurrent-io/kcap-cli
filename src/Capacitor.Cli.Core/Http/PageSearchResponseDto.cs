using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>The pages lane of <c>/api/search</c>. A server that predates the lane answers with no
/// <c>pages</c> key, so null here means "cannot search pages", not "found none".</summary>
public record PageSearchResponseDto([property: JsonPropertyName("pages")] List<PageSearchHitDto>? Pages);
