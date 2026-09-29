namespace Capacitor.Cli.Commands;

internal sealed record ForegroundPlan(
    List<List<ImportCommand.SessionClassification>> Chains,
    List<ImportCommand.SessionClassification>       Routed,
    ImportRunSelection                              Selection);
