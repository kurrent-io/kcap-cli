using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One artefact as the server describes it. Mirrors the server's wire shape rather than
/// referencing it: this binary ships independently and has to keep parsing what an older or newer
/// server sends.</summary>
public record ArtefactDto(
    [property: JsonPropertyName("artefact_id")]    string         ArtefactId,
    [property: JsonPropertyName("title")]          string         Title,
    [property: JsonPropertyName("owner_user_id")]  string         OwnerUserId,
    [property: JsonPropertyName("visibility")]     string         Visibility,
    [property: JsonPropertyName("latest_version")] int            LatestVersion,
    [property: JsonPropertyName("updated_at")]     DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("is_owner")]       bool           IsOwner,
    [property: JsonPropertyName("url")]            string         Url);

public record ArtefactGrantDto(
    [property: JsonPropertyName("grant_type")]   string GrantType,
    [property: JsonPropertyName("grantee_id")]   string GranteeId,
    [property: JsonPropertyName("grantee_name")] string GranteeName);

public record ArtefactDetailDto(
    [property: JsonPropertyName("artefact")] ArtefactDto             Artefact,
    [property: JsonPropertyName("grants")]   List<ArtefactGrantDto>? Grants);

public record ArtefactListDto([property: JsonPropertyName("artefacts")] List<ArtefactDto> Artefacts);

public record PublishArtefactBody(
    [property: JsonPropertyName("title")]       string                  Title,
    [property: JsonPropertyName("html")]        string                  Html,
    [property: JsonPropertyName("description")] string?                 Description,
    [property: JsonPropertyName("visibility")]  string?                 Visibility,
    [property: JsonPropertyName("grants")]      List<ArtefactGrantDto>? Grants,
    [property: JsonPropertyName("sources")]     List<string>?           Sources);

public record PublishArtefactVersionBody([property: JsonPropertyName("html")] string Html);

public record SetArtefactVisibilityBody(
    [property: JsonPropertyName("visibility")] string                  Visibility,
    [property: JsonPropertyName("grants")]     List<ArtefactGrantDto>? Grants);

/// <summary>A refusal the server named. <see cref="Limit"/> carries the ceiling that was hit, so the
/// command can say which one rather than making the caller find it by bisection.</summary>
public record ArtefactErrorDto(
    [property: JsonPropertyName("code")]    string  Code,
    [property: JsonPropertyName("message")] string  Message,
    [property: JsonPropertyName("limit")]   long?   Limit);

/// <summary>What a write came back as. A refusal is a value rather than an exception because every
/// one of these is something the caller can act on.</summary>
public abstract record ArtefactWriteResult {
    public sealed record Written(ArtefactDetailDto Detail) : ArtefactWriteResult;
    public sealed record Gone : ArtefactWriteResult;

    /// <summary>Refused for a reason the server named.</summary>
    public sealed record Refused(ArtefactErrorDto Error) : ArtefactWriteResult;

    /// <summary>No artefact under that id, or none this profile may see — the server does not
    /// distinguish the two, and neither does this.</summary>
    public sealed record NotFound : ArtefactWriteResult;

    /// <summary>Visible, but not this profile's to change.</summary>
    public sealed record NotYours : ArtefactWriteResult;
}
