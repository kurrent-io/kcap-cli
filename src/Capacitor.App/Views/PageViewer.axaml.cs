using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Hosts a ready page in a NativeWebView. The WebView exists only while the page is Ready, which
/// the view model reaches only where IWebViewAvailability allows it, so a runtime without a usable
/// WebView never constructs one. A page loads only into a native web view whose WebRTC is off, and
/// every handler the WebView raises catches what it throws: they run from native callbacks, where
/// an escaping exception ends the process.
public partial class PageViewer : UserControl {
    PageViewerViewModel? _vm;
    NativeWebView? _webView;
    PageBridge? _bridge;
    bool _attached;
    bool _webViewSafe;

    public PageViewer() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            if (_attached) Attach(DataContext as PageViewerViewModel);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Attach(DataContext as PageViewerViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
        ReplaceBridge(null);
    }

    void Attach(PageViewerViewModel? vm) {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        Show();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PageViewerViewModel.Html) or nameof(PageViewerViewModel.State)) Show();
    }

    void Show() {
        if (_vm is { State: PageViewerState.Ready, Html: { } html } vm) {
            var webView = EnsureWebView();
            // Until the native view exists and WebRTC is off; AdapterCreated shows the page then.
            if (!_webViewSafe) {
                ReplaceBridge(null);
                return;
            }
            ReplaceBridge(vm.CreateBridge(json => DeliverAsync(webView, json)));
            webView.NavigateToString(html, PageViewerViewModel.HostBase);
            return;
        }
        ReplaceBridge(null);
        WebHost.Content = null;
        _webView = null;
        _webViewSafe = false;
    }

    void ReplaceBridge(PageBridge? next) {
        var old = _bridge;
        _bridge = next;
        if (old is not null) _ = old.DisposeAsync().AsTask();
        next?.Start();
    }

    static Task DeliverAsync(NativeWebView webView, string json) =>
        Dispatcher.UIThread.InvokeAsync(() => webView.InvokeScript($"window.kcapDeliver && window.kcapDeliver({json})"));

    NativeWebView EnsureWebView() {
        if (_webView is not null) return _webView;
        var webView = new NativeWebView();
        webView.EnvironmentRequested += (_, e) => {
            try {
                if (e is AppleWKWebViewEnvironmentRequestedEventArgs apple) apple.NonPersistentDataStore = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
            }
        };
        webView.AdapterCreated += (_, e) => {
            try {
                if (!ReferenceEquals(webView, _webView)) return;
                if (e.TryGetPlatformHandle() is IAppleWKWebViewPlatformHandle apple && WebKitPeerConnection.TryDisable(apple.WKWebView)) {
                    _webViewSafe = true;
                    Show();
                } else {
                    Console.Error.WriteLine("kcap: could not turn WebRTC off in the page web view; pages open in the browser");
                    _vm?.WebViewUnusable();
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
                _vm?.WebViewUnusable();
            }
        };
        webView.AdapterDestroyed += (_, _) => {
            try {
                if (ReferenceEquals(webView, _webView)) {
                    _webViewSafe = false;
                    ReplaceBridge(null);
                }
                // A new native view replays the last load before AdapterCreated: make that a blank one.
                webView.NavigateToString("", PageViewerViewModel.HostBase);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view teardown failed: {ex.Message}");
            }
        };
        webView.NavigationStarted += (_, e) => {
            e.Cancel = true;
            try {
                e.Cancel = _vm?.CancelNavigation(e.Request) ?? true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page navigation check failed: {ex.Message}");
            }
        };
        webView.NewWindowRequested += (_, e) => {
            e.Handled = true;
            try {
                _vm?.OpenNewWindow(e.Request);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page new-window request failed: {ex.Message}");
            }
        };
        webView.WebMessageReceived += (_, e) => {
            try {
                if (_bridge is { } bridge && ReferenceEquals(webView, _webView)) _ = bridge.OnNativeMessageAsync(e.Body ?? "");
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page message failed: {ex.Message}");
            }
        };
        _webView = webView;
        WebHost.Content = webView;
        return webView;
    }
}
