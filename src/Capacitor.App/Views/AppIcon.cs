using Avalonia.Layout;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// Shared UI icon: a full 24px Lucide canvas scaled into a square slot, with round 2px
/// strokes. Use Size (12 for dense status rows, 14 by default, 16 for actions), rather
/// than stretching geometry bounds. Vendor logos and product branding stay separate.
public sealed class AppIcon : LucideIcon {
    public AppIcon() {
        Size = 14;
        StrokeWidth = 2;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }
}
