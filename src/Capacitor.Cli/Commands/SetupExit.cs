namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup</c> exits with, documented in its help and the README for scripts to branch on.</summary>
static class SetupExit {
    public const int Done = 0;

    /// <summary>Setup started changing the machine and did not finish; re-running it continues.</summary>
    public const int Failed = 1;

    /// <summary>Nothing was attempted: an argument was wrong, or an answer a run that cannot ask needs was missing.</summary>
    public const int Refused = 2;

    public const int Interrupted = 130;
}
