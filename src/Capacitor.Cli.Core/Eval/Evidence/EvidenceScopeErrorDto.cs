using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceScopeErrorDto([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("current_version")] string? CurrentVersion);
