namespace Capacitor.App.ViewModels.Onboarding;

/// One harness on the Done page's capture line: "turned on", or "needs you" while the login shell
/// cannot find kcap.
public sealed record CaptureItem(string Label, bool NeedsYou) {
    public string State => NeedsYou ? "needs you" : "turned on";
}
