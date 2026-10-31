namespace Capacitor.Cli.Commands;

/// <param name="PlanPath">An <see cref="ImportPlan"/> the child runs instead of importing everything.</param>
internal sealed record BackgroundImportRequest(
    string RunId, string ProfileName, string DefaultVisibility, string WorkingDirectory, string? PlanPath = null) {
    /// <summary>Whether the user agreed to have the remainder named by their own agent. A plan carries its
    /// own answer, so this applies only without one.</summary>
    public bool NameSessions { get; init; }

    /// <summary>What the foreground pass left out, so the remainder leaves it out too. A plan names its
    /// repositories outright, so this applies only without one.</summary>
    public ImportSkip Skip { get; init; } = ImportSkip.None;
}
