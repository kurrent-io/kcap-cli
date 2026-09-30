using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

internal sealed record HarnessTitleHook(
    [property: JsonPropertyName("session_id")] string          SessionId,
    [property: JsonPropertyName("title")]      string          Title,
    [property: JsonPropertyName("kind")]       string          Kind,
    [property: JsonPropertyName("changed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                               DateTimeOffset? ChangedAt);
