using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Capacitor.App.Views;

/// The product mark, loaded once. TrayIconRenderer composites Bitmap; the windows built in code
/// take WindowIcon. XAML windows name the same avares:// URI directly.
static class ProductIcon {
    const string AssetUri = "avares://Kurrent Capacitor/Assets/kcap-icon.png";

    static readonly Lazy<Bitmap> LazyBitmap = new(() => new Bitmap(AssetLoader.Open(new Uri(AssetUri))));
    static readonly Lazy<WindowIcon> LazyWindowIcon = new(() => new WindowIcon(LazyBitmap.Value));

    public static Bitmap Bitmap => LazyBitmap.Value;
    public static WindowIcon WindowIcon => LazyWindowIcon.Value;
}
