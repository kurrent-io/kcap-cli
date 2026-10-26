using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Config;

/// <summary>The one rule that turns the saved switch and the daemon's report into a state; the CLI and the
/// desktop app both render from it.</summary>
public static class ExperimentalFeatureState {
    public static (ExperimentalFeatureStateKind Kind, string? Reason) Classify(
        bool saved, bool daemonRunning, DaemonStatusDto? status, string featureId) {
        if (!daemonRunning) return (ExperimentalFeatureStateKind.DaemonNotRunning, null);

        var feature = status?.Experimental?.FirstOrDefault(f => string.Equals(f.Id, featureId, StringComparison.Ordinal));
        if (feature is null) return (ExperimentalFeatureStateKind.Unknown, null);

        if (feature.ResolvedSaved != saved) return (ExperimentalFeatureStateKind.Applying, null);
        if (feature.Active) {
            return feature.ServerAcknowledged
                ? (ExperimentalFeatureStateKind.Active, null)
                : (ExperimentalFeatureStateKind.AppliedLocally, null);
        }

        return saved
            ? (ExperimentalFeatureStateKind.OnNotActive, feature.Reason)
            : (ExperimentalFeatureStateKind.Off, null);
    }
}
