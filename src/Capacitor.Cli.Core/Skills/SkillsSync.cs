using System.Text;
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

/// <summary>A directory kcap wrote that is owed a deletion: the document it holds, the path, and
/// the skills root that authorises deleting it. The root travels with the path because containment
/// is defined against an anchor, and a path left over from a previous anchor cannot be authorised
/// by the current one. The document travels with it because the deletion is only due once its
/// replacement has actually been published, or the snapshot has stopped serving it at all.
///
/// <para><paramref name="Retired"/> marks a deletion an account change ordered. It has to outlive
/// the ledger's identity: the replacement catalogue is saved under the new account, so by the next
/// run nothing else remembers that these files belong to the previous one.</para></summary>
public sealed record PendingPrune(
    [property: JsonPropertyName("doc_id")]  Guid   DocId,
    [property: JsonPropertyName("path")]    string Path,
    [property: JsonPropertyName("root")]    string Root,
    [property: JsonPropertyName("retired")] bool   Retired = false);

public sealed record SkillsSnapshotResponse {
    [JsonPropertyName("etag")]   public string?              Etag   { get; init; }
    [JsonPropertyName("skills")] public SkillSnapshotItem[]? Skills { get; init; }
}

/// <summary>The sync ledger for one (repo, harness): which harness paths kcap owns. Pruning walks
/// THIS, never the skills root — user-authored skills and other plugins are untouchable.</summary>
public sealed record SkillsManifest {
    [JsonPropertyName("etag")]           public string?                Etag          { get; init; }
    [JsonPropertyName("synced_at")]      public DateTimeOffset?        SyncedAt      { get; init; }
    [JsonPropertyName("skills")]         public SkillsManifestEntry[]? Skills        { get; init; }
    [JsonPropertyName("anchor")]         public string?                Anchor        { get; init; }
    [JsonPropertyName("identity")]       public SkillsIdentity?        Identity      { get; init; }
    [JsonPropertyName("exposure")]       public string[]?              Exposure      { get; init; }
    [JsonPropertyName("pending")]        public bool                   Pending       { get; init; }
    [JsonPropertyName("pending_prunes")] public PendingPrune[]?        PendingPrunes { get; init; }
    /// <summary>The anchors this ledger has occupied that an outstanding row is still rooted at.
    /// Written from <see cref="Anchor"/> while it is live, because the builder overwrites that field
    /// with the current anchor — without this a row left at a previous one is authorisable for
    /// exactly one run. Dropped as soon as no row references it.</summary>
    [JsonPropertyName("prune_anchors")]  public string[]?              PruneAnchors  { get; init; }
}

public sealed record SkillsManifestEntry {
    [JsonPropertyName("doc_id")]       public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]         public required string Slug        { get; init; }
    [JsonPropertyName("version")]      public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")] public required string ContentHash { get; init; }
    [JsonPropertyName("path")]         public required string Path        { get; init; }
    // Hash of the rendered file as written, so a later sync can tell an edited or deleted
    // materialization from a served one. Null (an older manifest) reads as drifted.
    [JsonPropertyName("file_hash")]     public string? FileHash     { get; init; }
    // Server-provided provenance; derived from the request when an older server omits it.
    [JsonPropertyName("home")]          public string?             Home          { get; init; }
    [JsonPropertyName("applicability")] public SkillApplicability? Applicability { get; init; }
}

/// <summary>One harness tree skills materialize into, relative to a session's anchor. A null
/// <see cref="Vendor"/> marks a tree several harnesses read: the snapshot is fetched WITHOUT a
/// vendor, so unknown-excludes keeps every vendor-restricted doc out of it. <see cref="Consumers"/>
/// is the documented set this tree serves and decides adoption; <see cref="Readers"/> is the
/// measured set and is what a manifest records as exposure.</summary>
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

public sealed record SkillsSyncPlan(
    IReadOnlyList<SkillSnapshotItem>   Writes,
    IReadOnlyList<SkillsManifestEntry> Prunes,
    IReadOnlyList<SkillSnapshotItem>   Unchanged);

/// <summary>
/// Pure reconciliation of the manifest against a fresh snapshot. Identity is <c>doc_id</c> — the
/// server's stable manifest key — so a retitled doc (same id, new slug) is a rewrite at the new
/// path plus a prune of the old one, never an orphaned directory.
/// </summary>
public static class SkillsSyncPlanner {
    public static SkillsSyncPlan Plan(SkillsManifest? manifest, IReadOnlyList<SkillSnapshotItem> snapshot) {
        var owned = (manifest?.Skills ?? []).ToDictionary(e => e.DocId);
        var writes    = new List<SkillSnapshotItem>();
        var unchanged = new List<SkillSnapshotItem>();
        foreach (var item in snapshot) {
            if (owned.TryGetValue(item.DocId, out var have)
                    && have.Version == item.Version && have.ContentHash == item.ContentHash
                    && have.Slug == item.Slug)
                unchanged.Add(item);
            else
                writes.Add(item);
        }
        var live   = snapshot.Select(s => s.DocId).ToHashSet();
        var prunes = (manifest?.Skills ?? [])
            .Where(e => !live.Contains(e.DocId)
                        || snapshot.First(s => s.DocId == e.DocId).Slug != e.Slug)
            .ToList();
        return new SkillsSyncPlan(writes, prunes, unchanged);
    }

    /// <summary>The materialized SKILL.md: YAML frontmatter (name + when-to-use description as a
    /// double-quoted scalar) over the approved body.</summary>
    public static string RenderSkillFile(SkillSnapshotItem item) {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(item.Slug).Append('\n');
        var description = string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description!;
        sb.Append("description: ").Append(YamlQuote(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(item.Body);
        if (!item.Body.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>A slug usable as a single path segment: lowercase alphanumerics and dashes only —
    /// exactly the server's slug alphabet. Anything else (separators, dots, empty) is refused
    /// before it can reach a filesystem operation.</summary>
    public static bool IsSafeSlug(string slug) =>
        slug.Length is > 0 and <= 100 && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    static string YamlQuote(string value) {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
            sb.Append(c switch {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
