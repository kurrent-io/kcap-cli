namespace Capacitor.Cli.Commands;

/// What the daemon's stop acknowledgement says happened to one agent.
internal enum StopAckOutcome {
    Stopped,
    /// A review agent left running without --force: the documented default, never a failure.
    Skipped,
    /// The agent named was already gone when stopping everything.
    AlreadyStopped,
    NoSuchAgent,
    Failed,
}

internal static class StopAckOutcomeExtensions {
    public static bool IsFailure(this StopAckOutcome outcome) => outcome is StopAckOutcome.NoSuchAgent or StopAckOutcome.Failed;
}
