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

    /// <summary>The harness's turn cap: one turn per allowed tool call plus room for reasoning and the structured reply.</summary>
    public static int MaxTurns(int maxToolCalls) => maxToolCalls + 4;
}
