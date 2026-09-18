using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Capacitor.App.Materials;

namespace Capacitor.App.Controls;

public static class GlassFlyouts {
    /// The backdrop snapshot is per top-level window: a flyout in its own native popup window sees
    /// only itself. Under glass the popup is hosted in the owner's window instead, which clips it
    /// to that window; under Opaque it stays a native popup.
    public static void FollowMaterial(PopupFlyoutBase flyout, Control owner) =>
        flyout.Opening += (_, _) => flyout.Popup.ShouldUseOverlayLayer = MaterialScope.GetMaterial(owner).IsGlass();
}
