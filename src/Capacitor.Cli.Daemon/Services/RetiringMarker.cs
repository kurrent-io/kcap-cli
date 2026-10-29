using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The persisted commit of a rename fence, read by the next process under this name.</summary>
internal sealed record RetiringMarker(string? InstanceId, DateTimeOffset? CommittedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RetiringMarker))]
internal partial class AdmissionFenceJsonContext : JsonSerializerContext;
