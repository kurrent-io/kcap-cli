namespace Capacitor.Cli.Core.Config;

public static class ExperimentalFeatures {
    public static ExperimentalFeature CodexAppServer { get; } = new(
        "codex-app-server",
        "Codex app-server",
        "Run Codex reviewers and interactive Codex agents over codex app-server instead of the terminal. Needs Codex 0.146.0 or later.",
        AppliesLive: true);

    public static IReadOnlyList<ExperimentalFeature> All { get; } = [CodexAppServer];

    /// <summary>Ids are matched exactly, case included.</summary>
    public static ExperimentalFeature? Find(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}
