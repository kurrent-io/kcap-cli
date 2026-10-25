using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The rail's remembered shape for one user: the four controls plus the group keys they folded.
/// Value-equal on the folded keys as a sequence, so a persisted copy compares equal to the live one.
public sealed record RailPreferences(
        RailScope Scope, RailState State, RailGrouping Grouping, RailSort Sort, bool Descending,
        IReadOnlyList<string> Collapsed) {
    public static RailPreferences Default => new(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.LastUpdated, true, []);

    public bool Equals(RailPreferences? other) =>
        other is not null && Scope == other.Scope && State == other.State && Grouping == other.Grouping
        && Sort == other.Sort && Descending == other.Descending && Collapsed.SequenceEqual(other.Collapsed, StringComparer.Ordinal);

    public override int GetHashCode() {
        var hash = HashCode.Combine(Scope, State, Grouping, Sort, Descending, Collapsed.Count);
        foreach (var key in Collapsed) hash = HashCode.Combine(hash, key);
        return hash;
    }
}
