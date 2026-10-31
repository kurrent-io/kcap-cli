namespace Capacitor.App.ViewModels;

public enum PageViewerState {
    Loading,
    Ready,
    NotFound,
    SignedOut,
    Unreachable,
    /// No WebView on this runtime can confine a page, so it opens in the browser instead.
    NoWebView,
}
