namespace Capacitor.App.Services.LocalSetup;

/// The one result of a finished `kcap local upgrade`, decided after the child has exited.
public abstract record LocalUpgradeOutcome {
    // The exit codes LocalUpgradeCommand documents.
    const int UpgradedExit          = 0;
    const int FailedAfterSwitchExit = 2;
    const int NothingToDoExit       = 3;
    const int NotPublishedExit      = 4;

    public sealed record Upgraded(string? From, string? To) : LocalUpgradeOutcome;

    /// The stack is already at the bundled CLI's version or newer, or there is no installation.
    public sealed record NothingToDo(string? Reason) : LocalUpgradeOutcome;

    /// No bundle is published for this version yet; nothing changed.
    public sealed record NotPublished(string? Message) : LocalUpgradeOutcome;

    /// <param name="Switched">The new version is selected and left stopped; otherwise the old one was kept.</param>
    public sealed record Failed(
        string? Code, string Message, bool Switched, LocalInstallAffordance Affordance, string? From, string? To) : LocalUpgradeOutcome;

    /// The exit code decides the kind. A `switched` line makes any failure one after the switch: a child
    /// killed past it exits with the signal's code, and the new version is still selected.
    public static LocalUpgradeOutcome Settle(LocalUpgradeProgress progress, int exitCode, string? stderr) =>
        exitCode switch {
            UpgradedExit     => new Upgraded(progress.From, progress.To),
            NothingToDoExit  => new NothingToDo(progress.SkipReason),
            NotPublishedExit => new NotPublished(progress.FailedMessage),
            _ => new Failed(
                progress.FailedCode,
                progress.FailedMessage ?? stderr ?? $"The upgrade stopped before it finished (exit {exitCode}).",
                exitCode == FailedAfterSwitchExit || progress.FailedSwitched == true || progress.SwitchedTo is not null,
                LocalInstallAffordance.For(progress.FailedCode),
                progress.From,
                progress.To),
        };
}
