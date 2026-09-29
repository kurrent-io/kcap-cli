using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Skills;

/// <summary>One row of the server's versioned skills snapshot.</summary>
public sealed record SkillSnapshotItem {
    [JsonPropertyName("doc_id")]       public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]         public required string Slug        { get; init; }
    [JsonPropertyName("title")]        public required string Title       { get; init; }
    [JsonPropertyName("description")]  public string?         Description { get; init; }
    [JsonPropertyName("body")]         public required string Body        { get; init; }
    [JsonPropertyName("version")]      public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")]  public required string ContentHash   { get; init; }
    [JsonPropertyName("home")]          public string?              Home          { get; init; }
    [JsonPropertyName("applicability")] public SkillApplicability?  Applicability { get; init; }
}

/// <summary>The credential the snapshot was fetched under. A profile name is not identity: signing
/// in again replaces the credentials inside one profile and server.</summary>
public sealed record SkillsIdentity(
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("server")]  string Server);

public sealed record SkillsSnapshotResponse {
    [JsonPropertyName("etag")]   public string?              Etag   { get; init; }
    [JsonPropertyName("skills")] public SkillSnapshotItem[]? Skills { get; init; }
}

/// <summary>The ledger shape every installed release wrote: one entry per document, carrying the
/// path it was written to, and no identity or anchor at all. Read only to be converted — see
/// <see cref="SkillsLedgerFile"/>.</summary>
public sealed record SkillsManifest {
    [JsonPropertyName("etag")]      public string?                Etag     { get; init; }
    [JsonPropertyName("synced_at")] public DateTimeOffset?        SyncedAt { get; init; }
    [JsonPropertyName("skills")]    public SkillsManifestEntry[]? Skills   { get; init; }
}

public sealed record SkillsManifestEntry {
    [JsonPropertyName("doc_id")]       public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]         public required string Slug        { get; init; }
    [JsonPropertyName("version")]      public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")] public required string ContentHash { get; init; }
    [JsonPropertyName("path")]         public required string Path        { get; init; }
    /// <summary>Hash of the rendered file as written. Missing on an entry a run wrote before the
    /// hash existed, which is a claim nothing can vouch for.</summary>
    [JsonPropertyName("file_hash")]    public string?         FileHash    { get; init; }
}

/// <summary>One harness tree skills materialize into, relative to a session's anchor. A null
/// <see cref="Vendor"/> marks a tree several harnesses read: the snapshot is fetched WITHOUT a
/// vendor, so unknown-excludes keeps every vendor-restricted doc out of it. <see cref="Consumers"/>
/// is the documented set this tree serves and decides adoption; <see cref="Readers"/> is the
/// measured set and is what a ledger records as exposure.</summary>
public sealed record SkillsTarget(
    string Key, string RelativePath, string? Vendor,
    IReadOnlyList<HarnessId> Consumers, IReadOnlyList<HarnessId> Readers) {
    public string Root(string anchor) => Path.Combine(anchor, RelativePath);

    /// <summary>The user-global tree this target's copies were written into before materialization
    /// moved inside the checkout — the one root a legacy retirement may delete from. Required, and
    /// not derived from <see cref="RelativePath"/>, because two vendors relocate theirs through a
    /// documented environment override.</summary>
    public required string LegacyRoot { get; init; }
}
