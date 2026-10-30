using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Hosts a ready page in a NativeWebView. The WebView exists only while the page is Ready, which
/// the view model reaches only where IWebViewAvailability allows it, so a runtime without a usable
/// WebView never constructs one.
public partial class PageViewer : UserControl {
    PageViewerViewModel? _vm;
    NativeWebView? _webView;

    public PageViewer() {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as PageViewerViewModel);
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
        if (_vm is { State: PageViewerState.Ready, Html: { } html }) {
            EnsureWebView().NavigateToString(html, PageViewerViewModel.HostBase);
            return;
        }
        WebHost.Content = null;
        _webView = null;
    }

    NativeWebView EnsureWebView() {
        if (_webView is not null) return _webView;
        var webView = new NativeWebView();
        webView.EnvironmentRequested += (_, e) => {
            if (e is AppleWKWebViewEnvironmentRequestedEventArgs apple) apple.NonPersistentDataStore = true;
        };
        webView.NavigationStarted += (_, e) => e.Cancel = _vm?.CancelNavigation(e.Request) ?? true;
        webView.NewWindowRequested += (_, e) => {
            e.Handled = true;
            _vm?.OpenNewWindow(e.Request);
        };
        WebHost.Content = webView;
        return _webView = webView;
    }
}
