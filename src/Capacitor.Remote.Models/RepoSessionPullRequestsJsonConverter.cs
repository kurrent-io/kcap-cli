using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Reads a JSON null as withheld rather than as no value, which is what an absent field means.
public sealed class RepoSessionPullRequestsJsonConverter : JsonConverter<RepoSessionPullRequests> {
    public override bool HandleNull => true;

    public override RepoSessionPullRequests Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? RepoSessionPullRequests.Withheld
            : new(JsonSerializer.Deserialize(ref reader, RemoteModelsJsonContext.Default.RepoSessionPullRequestDtoArray) ?? []);

    public override void Write(Utf8JsonWriter writer, RepoSessionPullRequests? value, JsonSerializerOptions options) {
        if (value?.Items is not { } items) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, items.ToArray(), RemoteModelsJsonContext.Default.RepoSessionPullRequestDtoArray);
    }
}
