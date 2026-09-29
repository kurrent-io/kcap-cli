using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceReadErrorDto([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("detail")] string Detail);
