using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>What one owned path is. <see cref="Reserved"/> and <see cref="Unverified"/> grant no
/// deletion authority, never satisfy publication, and never make a conditional request eligible.
///
/// <para>Each name's spelling on the wire is pinned here: a ledger written by one build is read by
/// the next, and a value it cannot parse reads as a corrupt row.</para></summary>
public enum OwnedSkillState {
    /// <summary>A destination that is ours to write. A prepared operation means an attempt is in
    /// flight; none means it is awaiting a retry, because a reservation outlives the attempt that
    /// made it.</summary>
    [JsonStringEnumMemberName("reserved")] Reserved,

    /// <summary>Ours, verified against what we wrote.</summary>
    [JsonStringEnumMemberName("published")] Published,

    /// <summary>A claim whose bytes we cannot vouch for: reported, never deleted.</summary>
    [JsonStringEnumMemberName("unverified")] Unverified,

    /// <summary>A deletion we owe, against the cause that ordered it.</summary>
    [JsonStringEnumMemberName("owed")] Owed,

    /// <summary>A directory left standing because it holds something we did not write. It grants no
    /// authority over any future file at that path.</summary>
    [JsonStringEnumMemberName("settled")] Settled,
}
