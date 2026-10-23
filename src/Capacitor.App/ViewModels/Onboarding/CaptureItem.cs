namespace Capacitor.App.ViewModels.Onboarding;

/// One agent in the Done page's Connected agents card: connected, or "needs you" while the login shell
/// cannot find kcap.
public sealed record CaptureItem(string Label, bool NeedsYou) {
    public string State => NeedsYou ? "Needs you" : "Connected";
}
