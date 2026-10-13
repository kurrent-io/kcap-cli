namespace Capacitor.Cli.Commands;

/// <param name="PlanPath">An <see cref="ImportPlan"/> the child runs instead of importing everything.</param>
internal sealed record BackgroundImportRequest(
    string RunId, string ProfileName, string DefaultVisibility, string WorkingDirectory, string? PlanPath = null);
