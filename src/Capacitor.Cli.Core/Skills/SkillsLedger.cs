using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// The ownership record for one (worktree, target): one row per physical path, keyed by path.
/// Pruning walks THIS, never a skills root — user-authored skills and other plugins are
/// untouchable.
/// </summary>
public sealed record SkillsLedger {
    [JsonPropertyName("etag")]      public string?         Etag     { get; init; }
    [JsonPropertyName("synced_at")] public DateTimeOffset? SyncedAt { get; init; }

    /// <summary>The effective credential the snapshot was fetched under.</summary>
    [JsonPropertyName("identity")] public SkillsIdentity? Identity { get; init; }

    /// <summary>Every harness measured to read the tree this target writes to.</summary>
    [JsonPropertyName("exposure")] public string[]? Exposure { get; init; }

    [JsonPropertyName("owned")] public OwnedSkillRow[]? Owned { get; init; }

    /// <summary>The account an outstanding legacy retirement belongs to. Recorded before the local
    /// catalogue's identity is replaced, so the obligation survives a legacy ledger that cannot yet
    /// be read or migrated, and is retried before any fetch until it is discharged.</summary>
    [JsonPropertyName("legacy_retirement")] public SkillsIdentity? LegacyRetirement { get; init; }

    [JsonIgnore] public IReadOnlyList<OwnedSkillRow> Rows => Owned ?? [];
}
