using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Why a deletion is owed. The wire spelling of each name is part of the ledger's
/// persistence contract.</summary>
public enum SkillDeletionCause {
    /// <summary>A specific other row has been recorded as now serving this document.</summary>
    [JsonStringEnumMemberName("superseded")] Superseded,

    /// <summary>The snapshot stopped serving the document.</summary>
    [JsonStringEnumMemberName("revoked")] Revoked,

    /// <summary>An account transition ordered it. The row also carries the account it belongs to:
    /// the replacement catalogue is saved under the new one, so nothing else would remember whose
    /// files these are.</summary>
    [JsonStringEnumMemberName("retired")] Retired,
}
