using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// One physical path kcap has written or still owes something for. The ledger keys these by path,
/// so ownership of a path is read off the row rather than derived per operation from a collection
/// keyed by something else.
///
/// <para>Only the combinations <see cref="SkillsLedgerValidation"/> admits are legal; anything else
/// is a corrupt row, refused and reported.</para>
/// </summary>
public sealed record OwnedSkillRow {
    /// <summary>The directory, the skills root it sits in, and the anchor that authorised it — all
    /// recorded from the run's own resolved anchor while it was live.</summary>
    [JsonPropertyName("path")]   public required string  Path   { get; init; }
    [JsonPropertyName("root")]   public required string  Root   { get; init; }
    [JsonPropertyName("anchor")] public          string? Anchor { get; init; }

    [JsonPropertyName("origin")] public required SkillOrigin     Origin { get; init; }
    [JsonPropertyName("state")]  public required OwnedSkillState State  { get; init; }

    /// <summary>What we last successfully wrote. Null only if no write has ever completed
    /// here.</summary>
    [JsonPropertyName("confirmed")] public SkillReceipt? Confirmed { get; init; }

    /// <summary>Receipts handed over by a co-owner that relinquished this path. Whichever receipt
    /// the file actually matches is the one a deletion uses; choosing between hashes already
    /// recorded is not the same as learning one from disk.</summary>
    [JsonPropertyName("inherited")] public SkillReceipt[]? Inherited { get; init; }

    [JsonPropertyName("prepared")] public PreparedSkillWrite? Prepared { get; init; }

    [JsonPropertyName("cause")] public SkillDeletionCause? Cause { get; init; }

    [JsonPropertyName("identity_retired")] public SkillsIdentity? IdentityRetired { get; init; }

    /// <summary>Every hash this row may delete against.</summary>
    [JsonIgnore] public IEnumerable<SkillReceipt> Receipts =>
        Confirmed is null ? Inherited ?? [] : [Confirmed, .. Inherited ?? []];

    /// <summary>The document this row is about: what we last wrote, or failing that what we were
    /// about to write. Null on a row that never got either.</summary>
    [JsonIgnore] public SkillDocument? Document => Confirmed?.Document ?? Prepared?.Intended.Document;

    [JsonIgnore] public bool HoldsAClaim => State is OwnedSkillState.Published or OwnedSkillState.Owed;
}
