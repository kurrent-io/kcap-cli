using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Capacitor.App.Views;

/// The product mark, loaded once. TrayIconRenderer composites TrayBitmap, the small-size variant
/// with heavier strokes; the windows built in code take WindowIcon. XAML windows name the same
/// avares:// URI directly.
static class ProductIcon {
    const string AssetUri = "avares://kcap-desktop/Assets/kcap-icon.png";
    const string TrayAssetUri = "avares://kcap-desktop/Assets/kcap-tray-icon.png";

    static readonly Lazy<Bitmap> LazyBitmap = new(() => Load(AssetUri));
    static readonly Lazy<Bitmap> LazyTrayBitmap = new(() => Load(TrayAssetUri));
    static readonly Lazy<WindowIcon> LazyWindowIcon = new(() => new WindowIcon(LazyBitmap.Value));

    public static Bitmap TrayBitmap => LazyTrayBitmap.Value;
    public static WindowIcon WindowIcon => LazyWindowIcon.Value;

    static Bitmap Load(string uri) => new(AssetLoader.Open(new Uri(uri)));
}
