using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>The document a receipt served, as the snapshot described it.</summary>
public sealed record SkillDocument {
    [JsonPropertyName("doc_id")]        public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]          public required string Slug        { get; init; }
    [JsonPropertyName("version")]       public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")]  public required string ContentHash { get; init; }
    /// <summary>Server-provided provenance; derived from the request when an older server omits
    /// it.</summary>
    [JsonPropertyName("home")]          public string?             Home          { get; init; }
    [JsonPropertyName("applicability")] public SkillApplicability? Applicability { get; init; }

    public static SkillDocument Of(SkillSnapshotItem item, string repoHome) => new() {
        DocId = item.DocId, Slug = item.Slug, Version = item.Version,
        ContentHash = item.ContentHash, Home = item.Home ?? repoHome,
        Applicability = item.Applicability,
    };

    /// <summary>Whether the snapshot still serves exactly what this describes. The planner compares
    /// these three and nothing else; the file hash answers a different question.</summary>
    public bool Serves(SkillSnapshotItem item) =>
        DocId == item.DocId && Version == item.Version
        && string.Equals(ContentHash, item.ContentHash, StringComparison.Ordinal)
        && string.Equals(Slug, item.Slug, StringComparison.Ordinal);
}
