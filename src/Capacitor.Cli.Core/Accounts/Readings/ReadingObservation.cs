namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>One vendor response. <see cref="Windows"/> carry no <c>ConfirmedAt</c>: the merge stamps
/// <see cref="ProducedAt"/> when <see cref="Fresh"/>. <see cref="LimitStateKnown"/> is false for a
/// sparse notification whose missing fields mean unavailable rather than cleared, and such an
/// observation never clears a block.</summary>
public sealed record ReadingObservation(
    DateTimeOffset                  ProducedAt,
    bool                            Fresh,
    IReadOnlyList<UsageWindowDto>   Windows,
    string?                         Plan,
    CreditsDto?                     Credits,
    IReadOnlyList<LimitSignal>      Limits,
    bool                            LimitReachedReported,
    bool                            LimitStateKnown);
