namespace Capacitor.Cli.Commands;

/// <summary>The runner's totalized result. Null <see cref="Selection"/> means <see cref="Fault"/>
/// struck before the selection checkpoint; null <see cref="Outcome"/> means the pass never
/// finished.</summary>
internal sealed record SetupImportRun(
    int                             ExitCode,
    ImportRunSelection?             Selection,
    ImportCommand.ImportRunOutcome? Outcome,
    Exception?                      Fault);
