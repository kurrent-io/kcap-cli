using Avalonia.Platform;

namespace Capacitor.App.Services;

/// A page is hosted in-app only where the viewer is proven to cancel a navigation in any frame
/// before its request is sent. The package raises NavigationStarted from WKWebView's policy
/// decision, which covers subframes; its WebView2 adapter listens to the main frame only, and
/// WebKitGTK is unproven, so those runtimes open pages in the browser.
public sealed class WebViewAvailability : IWebViewAvailability {
    public bool IsAvailable { get; } =
        OperatingSystem.IsMacOS() && WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView).IsSupported;
}
