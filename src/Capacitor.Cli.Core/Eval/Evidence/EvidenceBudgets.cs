namespace Capacitor.Cli.Core.Eval.Evidence;

public static class EvidenceBudgets {
    // Equal to the server's JudgeRequestSizing.MaxFirstViewBytes: after the first view's envelope it still leaves a whole
    // 65 536-byte section for the first strategy section.
    public const int    FirstViewBytes       = 196_608;
    public const int    OrientationBytes     = 65_536;
    public const double SoftDeadlineFraction = 0.8;
    public const int    MaxSpendUsdCents     = 100;
    public const int    MaxCitations         = 200;
    public const int    MaxCiteHandleBytes   = 16;

    // The server's ceiling of a first view with no sections, and its least section budget.
    public const int FirstViewEnvelopeBytes = 8_192;
    public const int MinSectionBytes        = 65_536;

    /// <summary>The budget the server gives a view's first section at its worst-case envelope, or null when no section can run:
    /// sections run only while a whole page remains after the envelope, each at min(remaining, max(page, share × remaining)).</summary>
    public static int? FirstSectionBudget(int viewBytes, int sharePercent) {
        var remaining = viewBytes - FirstViewEnvelopeBytes;
        if (remaining < MinSectionBytes) return null;
        return (int)Math.Min(remaining, Math.Max(MinSectionBytes, (long)remaining * sharePercent / 100));
    }

    /// <summary>The harness's turn cap: one turn per allowed tool call plus room for reasoning and the structured reply.</summary>
    public static int MaxTurns(int maxToolCalls) => maxToolCalls + 4;
}
