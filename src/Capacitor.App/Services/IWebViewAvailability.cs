namespace Capacitor.App.Services;

/// Whether this runtime has a WebView that can host a kcap page in the app.
public interface IWebViewAvailability {
    bool IsAvailable { get; }
}
