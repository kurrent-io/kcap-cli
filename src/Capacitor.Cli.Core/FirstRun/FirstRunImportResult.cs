namespace Capacitor.Cli.Core.FirstRun;

/// <summary>What the foreground import passes moved, and what was left for the background.</summary>
/// <param name="Totals">Null when a pass produced no accounting at all: its sessions are unaccounted,
/// so the caller reports <see cref="FirstRunImportOutcomeReasons.RunFailed"/> rather than a surviving
/// pass's figures.</param>
/// <param name="Background">A <see cref="FirstRunImportBackground"/> token.</param>
/// <param name="HandoffPrompt">Mutually exclusive with <paramref name="HandoffSuppressed"/>.</param>
public sealed record FirstRunImportResult(
    FirstRunImportTotals? Totals,
    string?               Background          = null,
    int?                  BackgroundRemaining = null,
    string?               HandoffPrompt       = null,
    string?               HandoffSuppressed   = null) {
    public static FirstRunImportResult Lost { get; } = new((FirstRunImportTotals?)null);
}
