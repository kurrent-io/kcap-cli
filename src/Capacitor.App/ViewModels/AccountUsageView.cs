namespace Capacitor.App.ViewModels;

public enum UsageTone { Neutral, Warning, Danger, Unknown }

/// <see cref="Percent"/> is null for an expired window, whose usage is unknown rather than zero.
public sealed record UsageBar(string Label, int? Percent, string PercentText, UsageTone Tone, string? ResetText);

/// <see cref="LineText"/> replaces the bars when usage is missing or unknown. Equality compares
/// <see cref="Bars"/> element-wise so a re-projection that changed nothing is not a change.
public sealed record AccountUsageView(
    IReadOnlyList<UsageBar> Bars,
    string?                 LineText,
    string?                 PerModelText,
    string?                 BlockedText,
    string?                 NextResetText,
    string?                 AgeText,
    bool                    Ready
) {
    public bool Equals(AccountUsageView? other) =>
        other is not null
     && Bars.SequenceEqual(other.Bars)
     && LineText == other.LineText
     && PerModelText == other.PerModelText
     && BlockedText == other.BlockedText
     && NextResetText == other.NextResetText
     && AgeText == other.AgeText
     && Ready == other.Ready;

    public bool DrawnEquals(AccountUsageView other) =>
        Bars.SequenceEqual(other.Bars)
     && LineText == other.LineText
     && PerModelText == other.PerModelText
     && BlockedText == other.BlockedText
     && NextResetText == other.NextResetText;

    /// Equality over what the launcher draws: age and readiness do not change its rows.
    internal static bool DrawnUsageEquals(IReadOnlyDictionary<string, AccountUsageView> a, IReadOnlyDictionary<string, AccountUsageView> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.DrawnEquals(other));

    public override int GetHashCode() {
        var hash = new HashCode();
        foreach (var bar in Bars) hash.Add(bar);
        hash.Add(LineText);
        hash.Add(PerModelText);
        hash.Add(BlockedText);
        hash.Add(NextResetText);
        hash.Add(AgeText);
        hash.Add(Ready);
        return hash.ToHashCode();
    }
}
