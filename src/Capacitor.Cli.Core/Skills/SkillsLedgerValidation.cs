namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// The boundary every row crosses before anything acts on it. The five states admit exactly the
/// field combinations below; anything else is a corrupt row, which is refused, reported and acted
/// on in no way at all.
/// </summary>
public static class SkillsLedgerValidation {
    /// <summary>Why this row is illegal, or null when it is one of the legal combinations.</summary>
    public static string? Reject(OwnedSkillRow row) =>
        row.Path.Length == 0 ? "it records no path"
        : row.Root.Length == 0 ? "it records no skills root"
        : Defined(row) is { } undefined ? undefined
        : row.Origin == SkillOrigin.Legacy && row.Anchor is not null
            ? "a legacy row is authorised by its vendor root, so it may not record an anchor"
        : Shape(row) ?? Cause(row) ?? Evidence(row);

    public static bool IsLegal(OwnedSkillRow row) => Reject(row) is null;

    /// <summary>Membership, checked before any shape rule reads these as a case. The converter
    /// admits a number for any of them, and a value outside the table would otherwise fall through
    /// a default branch and be acted on as one that is inside it.</summary>
    static string? Defined(OwnedSkillRow row) =>
        !Enum.IsDefined(row.State) ? "its state is not one of the five"
        : !Enum.IsDefined(row.Origin) ? "its origin names no known authority"
        : row.Cause is { } cause && !Enum.IsDefined(cause) ? "its cause is not one of the three"
        : null;

    static string? Shape(OwnedSkillRow row) => row.State switch {
        OwnedSkillState.Reserved when row.Confirmed is not null =>
            "a reservation cannot carry a confirmed receipt",
        OwnedSkillState.Published when row.Confirmed is null =>
            "a published row needs the receipt it was published against",
        OwnedSkillState.Unverified when row.Confirmed is not null || row.Prepared is not null =>
            "an unverified row vouches for nothing, so it carries neither a receipt nor an operation",
        OwnedSkillState.Settled when row.Confirmed is not null || row.Prepared is not null =>
            "a settled row holds no claim, so it carries neither a receipt nor an operation",
        _ => null,
    };

    static string? Cause(OwnedSkillRow row) =>
        row.State == OwnedSkillState.Owed
            ? row.Cause is null ? "a deletion is owed without a cause" : null
            : row.Cause is not null ? "a cause is recorded where no deletion is owed" : null;

    static string? Evidence(OwnedSkillRow row) =>
        row.IdentityRetired is not null && row.Cause != SkillDeletionCause.Retired
            ? "a retired account is recorded against a deletion no account change ordered"
        : row.Cause == SkillDeletionCause.Retired && row.IdentityRetired is null
            ? "a retired deletion does not say whose files it is for"
        : row.Inherited is { Length: > 0 } && row.Confirmed is null
            ? "a receipt was inherited onto a row that holds none of its own"
        : null;
}
