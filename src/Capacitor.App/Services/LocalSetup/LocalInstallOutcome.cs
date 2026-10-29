namespace Capacitor.App.Services.LocalSetup;

/// The one result of a finished run, decided after the child has exited.
public abstract record LocalInstallOutcome {
    public sealed record Installed(string ServerUrl, string Plan) : LocalInstallOutcome;

    public sealed record Failed(string? Code, string Message, LocalInstallAffordance Affordance, bool LockedToLocal) : LocalInstallOutcome;

    /// A `committed` or `reused` line settles Installed whatever the exit code: a cancel after the
    /// commit boundary exits non-zero after `committed`.
    public static LocalInstallOutcome Settle(LocalSetupProgress progress, int exitCode, bool lockedToLocal) =>
        progress.SawSuccess
            ? new Installed(progress.ServerUrl ?? "", progress.Plan)
            : new Failed(
                progress.FailedCode,
                progress.FailedMessage ?? $"The install stopped before it finished (exit {exitCode}).",
                LocalInstallAffordance.For(progress.FailedCode),
                lockedToLocal);
}
