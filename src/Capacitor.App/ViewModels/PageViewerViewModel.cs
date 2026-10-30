using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One kcap page shown in the app: its content read, the host document the WebView loads, and the
/// navigation rule that keeps the WebView on that document.
public sealed class PageViewerViewModel : ReactiveObject {
    /// The base the host document loads under. A navigation to it, or to a frame's srcdoc, never
    /// leaves the process; anything else might, so the view cancels it.
    public static readonly Uri HostBase = new("about:blank");
    const string SrcdocUrl = "about:srcdoc";

    readonly IPageSource _source;
    readonly IUrlOpener _opener;
    readonly bool _webViewAvailable;
    int _generation;

    public PageViewerViewModel(IPageSource source, string artefactId, string browserUrl, IUrlOpener opener, TimeProvider time, bool webViewAvailable) {
        _source = source;
        ArtefactId = artefactId;
        BrowserUrl = browserUrl;
        _opener = opener;
        Time = time;
        _webViewAvailable = webViewAvailable;
        _state = webViewAvailable ? PageViewerState.Loading : PageViewerState.NoWebView;
        OpenInBrowserCommand = ReactiveCommand.Create(() => LinkPolicy.Open(_opener, BrowserUrl));
        ReloadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
    }

    public string ArtefactId { get; }
    public string BrowserUrl { get; }
    public TimeProvider Time { get; }

    PageViewerState _state;
    public PageViewerState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(Notice));
        }
    }

    public bool IsReady => State == PageViewerState.Ready;

    public string? Notice => State switch {
        PageViewerState.Loading     => "Loading page…",
        PageViewerState.NotFound    => "This page is gone, or you can't see it.",
        PageViewerState.SignedOut   => "Sign in to see this page.",
        PageViewerState.Unreachable => "Couldn't reach the server for this page.",
        PageViewerState.NoWebView   => "Pages open in your browser on this system.",
        _                           => null,
    };

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string? _html;
    /// The host document for the WebView; null after a read that found no page.
    public string? Html { get => _html; private set => this.RaiseAndSetIfChanged(ref _html, value); }

    public ArtefactDto? Page { get; private set; }
    public int Version { get; private set; }

    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }

    /// Reads the page and applies the result only if no later load has started meanwhile.
    public async Task LoadAsync(CancellationToken ct) {
        if (!_webViewAvailable) {
            State = PageViewerState.NoWebView;
            return;
        }
        var generation = ++_generation;
        State = PageViewerState.Loading;
        var read = await _source.ReadContentAsync(ArtefactId, null, ct);
        if (generation != _generation) return;

        if (read is PageContentRead.Found found) {
            Page = found.Page;
            Version = found.Version;
            Title = found.Page.Title;
            Html = PageHostDocument.Build(found.Html);
            State = PageViewerState.Ready;
            return;
        }
        Html = null;
        State = read switch {
            PageContentRead.NotFound  => PageViewerState.NotFound,
            PageContentRead.SignedOut => PageViewerState.SignedOut,
            _                         => PageViewerState.Unreachable,
        };
    }

    /// True when the WebView must not perform this navigation. Only the host document and its
    /// frames' srcdoc load in place; every other target, in any frame, goes to the browser instead.
    public bool CancelNavigation(Uri? request) {
        var url = request?.OriginalString;
        if (url == HostBase.OriginalString || url == SrcdocUrl) return false;
        OpenNewWindow(request);
        return true;
    }

    public void OpenNewWindow(Uri? request) =>
        LinkPolicy.Open(_opener, request is { IsAbsoluteUri: true } ? request.AbsoluteUri : null);
}
