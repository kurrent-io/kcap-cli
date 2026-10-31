using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts.Readings;

static class ReadingFixtures {
    public static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    public static UsageWindowDto W(string limitId, int windowMinutes, double usedPercent, DateTimeOffset? resetsAt, DateTimeOffset? confirmedAt = null) =>
        new(limitId, windowMinutes, usedPercent, resetsAt, confirmedAt);

    public static ReadingObservation Obs(
        int               at,
        bool              fresh,
        UsageWindowDto[]? windows       = null,
        LimitSignal[]?    limits        = null,
        bool              known         = true,
        string?           plan          = null,
        CreditsDto?       credits       = null,
        bool              reached       = false
    ) =>
        new(At(at), fresh, windows ?? [], plan, credits, limits ?? [], reached, known);

    public static AccountReadingDto Apply(params ReadingObservation[] observations) =>
        observations.Aggregate(ReadingMerge.Empty(1), ReadingMerge.Apply);

    public static UsageWindowDto Window(AccountReadingDto reading, string limitId, int windowMinutes) =>
        reading.Windows.Single(w => w.LimitId == limitId && w.WindowMinutes == windowMinutes);
}
