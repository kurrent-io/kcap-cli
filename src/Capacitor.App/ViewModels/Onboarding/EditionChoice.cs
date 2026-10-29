namespace Capacitor.App.ViewModels.Onboarding;

/// The first screen's answer. ExistingWorkspace is not an edition, but it is the funnel's third branch.
public enum EditionChoice { Local, Cloud, ExistingWorkspace }

public static class EditionChoiceExtensions {
    /// The telemetry value; dashboards key on it, so it never changes.
    public static string Wire(this EditionChoice choice) => choice switch {
        EditionChoice.Local => "local",
        EditionChoice.Cloud => "cloud",
        _                   => "existing_workspace",
    };
}
