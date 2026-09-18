using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Capacitor.App.Controls;
using Capacitor.App.Materials;

/// The two shapes the probe measures. Neither ships: glass in a flyout's popup never receives a
/// backdrop snapshot, so the app keeps its flyouts opaque and these live here with the probe.
static class GlassFlyouts {
    /// The backdrop snapshot is per top-level window: a flyout in its own native popup window sees
    /// only itself. Under glass the popup is hosted in the owner's window instead, which clips it
    /// to that window; under Opaque it stays a native popup.
    public static void FollowMaterial(PopupFlyoutBase flyout, Control owner) =>
        flyout.Opening += (_, _) => flyout.Popup.ShouldUseOverlayLayer = MaterialScope.GetMaterial(owner).IsGlass();

    /// The flyout's content wrapped in a panel that draws the glass, rather than a glass layer in
    /// the presenter's own template.
    public static Surface Panel(Control content) => new() { Classes = { "panel" }, Content = content };
}
