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

    readonly IPageContentSource _source;
    readonly IUrlOpener _opener;
    readonly bool _webViewAvailable;
    int _generation;

    public PageViewerViewModel(IPageContentSource source, string artefactId, string browserUrl, IUrlOpener opener, TimeProvider time, bool webViewAvailable) {
        _source = source;
        ArtefactId = artefactId;
        BrowserUrl = browserUrl;
        _opener = opener;
        Time = time;
        _webViewAvailable = webViewAvailable;
        _state = webViewAvailable ? PageViewerState.Loading : PageViewerState.NoWebView;
        OpenInBrowserCommand = ReactiveCommand.Create(() => LinkPolicy.Open(_opener, BrowserUrl));
        OpenPendingCommand = ReactiveCommand.Create(OpenPending);
        DismissPendingCommand = ReactiveCommand.Create(() => { PendingExternalUrl = null; });
        ReloadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        ReloadCommand.ThrownExceptions.Subscribe(ex => {
            Console.Error.WriteLine($"kcap: page reload failed: {ex.Message}");
            State = PageViewerState.Unreachable;
        });
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
            this.RaisePropertyChanged(nameof(CanRetry));
            this.RaisePropertyChanged(nameof(Notice));
        }
    }

    public bool IsReady => State == PageViewerState.Ready;
    public bool CanRetry => State is PageViewerState.Unreachable or PageViewerState.SignedOut;

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
    /// This load's proof for the bridge, bound into Html's host script; null without a page.
    public string? Nonce { get; private set; }
    public PageResultsAccess Access { get; private set; } = PageResultsAccess.Unknown;

    /// A link the page tried to follow, held until the user opens or dismisses it: a page's script
    /// must never reach the browser without a click in the app.
    string? _pendingExternalUrl;
    public string? PendingExternalUrl {
        get => _pendingExternalUrl;
        private set {
            if (_pendingExternalUrl == value) return;
            this.RaiseAndSetIfChanged(ref _pendingExternalUrl, value);
            this.RaisePropertyChanged(nameof(PendingPrompt));
            this.RaisePropertyChanged(nameof(HasPending));
        }
    }

    public bool HasPending => PendingExternalUrl is not null;

    public string? PendingPrompt => PendingExternalUrl is { } url
        ? $"This page wants to open {(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url)}"
        : null;

    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPendingCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissPendingCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }

    /// Reads the page and applies the result only if no later load has started meanwhile.
    public async Task LoadAsync(CancellationToken ct) {
        if (IsClosed) return;
        if (!_webViewAvailable) {
            State = PageViewerState.NoWebView;
            return;
        }
        var generation = ++_generation;
        var previous = State;
        PendingExternalUrl = null;
        State = PageViewerState.Loading;
        PageContentRead read;
        try {
            read = await _source.ReadContentAsync(ArtefactId, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            Abandon();
            return;
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page read failed: {ex.Message}");
            read = new PageContentRead.Unreachable();
        }
        if (generation != _generation) return;

        if (read is PageContentRead.Found found) {
            // Read before the page loads: its bridge must exist by the time the page says ready.
            PageResultsAccess access;
            try {
                access = await ReadAccessAsync(found.Page, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                Abandon();
                return;
            }
            if (generation != _generation) return;
            Page = found.Page;
            Version = found.Version;
            Access = access;
            Nonce = PageHostDocument.NewNonce();
            Title = found.Page.Title;
            Html = PageHostDocument.Build(found.Html, Nonce);
            State = PageViewerState.Ready;
            return;
        }
        Nonce = null;
        Html = null;
        State = read switch {
            PageContentRead.NotFound  => PageViewerState.NotFound,
            PageContentRead.SignedOut => PageViewerState.SignedOut,
            _                         => PageViewerState.Unreachable,
        };

        void Abandon() {
            if (generation == _generation) State = previous == PageViewerState.Loading ? PageViewerState.Unreachable : previous;
        }
    }

    async Task<PageResultsAccess> ReadAccessAsync(ArtefactDto page, CancellationToken ct) {
        try {
            return await _source.ResultsAccessAsync(ArtefactId, page, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: page results access read failed: {ex.Message}");
            return PageResultsAccess.Unknown;
        }
    }

    public bool IsClosed { get; private set; }

    /// Drops the loaded page so a view still holding this model releases its bridge and WebView,
    /// and no later load applies.
    public void Close() {
        if (IsClosed) return;
        IsClosed = true;
        _generation++;
        PendingExternalUrl = null;
        Page = null;
        Nonce = null;
        Html = null;
    }

    /// The credential lapsed: the page and its bridge go, so nothing keeps answering for a viewer
    /// who is no longer signed in. Reload reads it again.
    public void SignedOut() {
        if (IsClosed || State == PageViewerState.NoWebView) return;
        _generation++;
        PendingExternalUrl = null;
        Page = null;
        Nonce = null;
        Html = null;
        State = PageViewerState.SignedOut;
    }

    /// The WebView cannot be made safe for a page here, so the page is offered in the browser.
    public void WebViewUnusable() {
        _generation++;
        Nonce = null;
        Html = null;
        State = PageViewerState.NoWebView;
    }

    /// The bridge for the page this load found, answering only messages carrying its nonce; null
    /// when no page is loaded. The caller starts and disposes it.
    public PageBridge? CreateBridge(Func<string, Task> postToPage) =>
        Page is { } page && Nonce is { } nonce
            ? new PageBridge(_source, page, Version, Access, nonce, postToPage, Time)
            : null;

    /// True when the WebView must not perform this navigation. Only the host document and its
    /// frames' srcdoc load in place; any other target, in any frame, is offered for the browser.
    public bool CancelNavigation(Uri? request) {
        var url = request?.OriginalString;
        if (url == HostBase.OriginalString || url == SrcdocUrl) return false;
        OpenNewWindow(request);
        return true;
    }

    public void OpenNewWindow(Uri? request) {
        var url = request is { IsAbsoluteUri: true } ? request.AbsoluteUri : null;
        if (LinkPolicy.IsOpenable(url)) PendingExternalUrl = url;
    }

    void OpenPending() {
        var url = PendingExternalUrl;
        PendingExternalUrl = null;
        LinkPolicy.Open(_opener, url);
    }
}
