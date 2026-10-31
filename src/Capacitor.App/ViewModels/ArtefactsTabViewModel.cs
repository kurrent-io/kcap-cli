using System.Reactive;
using System.Reactive.Concurrency;
using System.Security.Cryptography;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The Artefacts tab: the documents a session declared or wrote, read from plan-artifacts with
/// their bodies, the published pages citing the session, and the one document or page that is open. The session id is
/// each read's identity; a lease owns one id, its cancellation and its pending read, a result applies
/// only for the current lease, and every lease transition happens on the UI thread.
public sealed class ArtefactsTabViewModel : ReactiveObject {
    readonly IPlanArtifactSource? _source;
    readonly SingleFlightReader<(string SessionId, string? Root), PlanArtifactsRead> _reads;
    readonly SingleFlightReader<string, SessionPagesRead> _pageReads;
    sealed record DriftRead(DriftState State);
    readonly SingleFlightReader<(DocumentRow Row, string? Root), DriftRead> _driftReads;
    readonly PlanActivity _activity;
    readonly TimeProvider _time;
    readonly IUrlOpener? _opener;
    readonly IPageViewer? _pageViewer;
    readonly IPageContentSource? _pageContent;
    readonly PageLinkRouter? _pageLinks;
    CancellationTokenSource? _viewerLifetime;
    int _viewerListedVersion;
    const long MaxHashedBytes = 4 * 1024 * 1024;

    readonly Func<string, Stream?> _openWorkingCopy;
    readonly AvaloniaList<DocumentRow> _documents = [];
    readonly AvaloniaList<PageRow> _pages = [];
    readonly ITimer _settle;
    bool _tornDown;
    string? _pendingPath;
    bool _documentsFailed;
    bool _pagesFailed;
    /// The kind of the last pages answer this lease applied; null until one lands.
    SessionPlansReadKind? _pagesKind;

    public IAvaloniaReadOnlyList<DocumentRow> Documents => _documents;
    public IAvaloniaReadOnlyList<PageRow> Pages => _pages;
    /// The last applied read of either list could not reach the server, so the pane's poll retries it
    /// even with the tab hidden.
    internal bool LastReadFailed => _documentsFailed || _pagesFailed;
    public bool HasDocuments => _documents.Count > 0;
    public bool HasPages => _pages.Count > 0;
    /// An open page counts: a link can open one this session does not list.
    public bool HasAny => HasDocuments || HasPages || PageViewer is not null;
    /// True while a session lease is open and has not yet applied a result — distinct from a settled empty list.
    public bool IsLoading => _reads.IsLoading;
    public bool IsLoadingPages => _pageReads.IsLoading;
    /// Right-pane prompt when nothing is open: loading, settled empty, or pick a row.
    public string EmptyReaderNote => IsLoading ? "Loading documents…"
        : HasPages && PagesOpenInApp ? HasDocuments ? "Select a document or page." : "Select a page to open it."
        : HasDocuments ? "Select a document to read it."
        : HasPages ? "Pages open in your browser."
        : "No documents yet.";

    public bool ShowsPagesSection => _pageReads.HasCurrent;
    /// Why the pages list is empty. A server that cannot answer for this session is not a session
    /// with no pages, so the two never share a note.
    public string PagesNote => HasPages || !_pageReads.HasCurrent ? ""
        : _pagesKind switch {
            null => "Loading pages…",
            SessionPlansReadKind.Ready => "No pages published from this session.",
            SessionPlansReadKind.SignedOut => "Sign in to see this session's pages.",
            SessionPlansReadKind.Unreachable => "Couldn't reach the server to list pages.",
            _ => "Pages aren't available for this session.",
        };
    public bool HasPagesNote => PagesNote.Length > 0;

    (string Path, string? Hash, DriftState State)? _lastDrift;
    DocumentRow? _selected;
    public DocumentRow? Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }

    DocumentReaderViewModel? _reader;
    public DocumentReaderViewModel? Reader {
        get => _reader;
        private set {
            this.RaiseAndSetIfChanged(ref _reader, value);
            this.RaisePropertyChanged(nameof(ShowsReaderNote));
        }
    }

    PageViewerViewModel? _openPage;
    /// The page open in the right pane in place of the reader; at most one of the two is set.
    public PageViewerViewModel? PageViewer {
        get => _openPage;
        private set {
            this.RaiseAndSetIfChanged(ref _openPage, value);
            this.RaisePropertyChanged(nameof(ShowsReaderNote));
            this.RaisePropertyChanged(nameof(HasAny));
        }
    }

    public bool ShowsReaderNote => Reader is null && PageViewer is null;
    /// Page rows open in the right pane rather than the browser.
    public bool PagesOpenInApp => _pageViewer is InAppPageViewer;

    bool _isShown;
    /// Set by the workspace while the tab is the active one; the pane's poll refreshes only then.
    public bool IsShown { get => _isShown; set => this.RaiseAndSetIfChanged(ref _isShown, value); }

    public ReactiveCommand<DocumentRow, Unit> SelectCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<PageRow, Unit> OpenPageCommand { get; }
    /// Something asked for the tab: a card's Open, a pane row, the pane summary, a page link.
    public event Action? OpenRequested;

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _reads.PendingReadForTesting;
    internal Task? PendingPagesForTesting => _pageReads.PendingReadForTesting;
    internal Task? PendingDriftForTesting => _driftReads.PendingReadForTesting;
    internal Task? PendingPageLoadForTesting { get; private set; }

    public ArtefactsTabViewModel(IPlanArtifactSource? source, PlanActivity activity, TimeProvider time, Func<string, Stream?>? openWorkingCopy = null,
            IUrlOpener? opener = null, IPageViewer? pageViewer = null, IPageContentSource? pageContent = null,
            PageLinkRouter? pageLinks = null, IWebViewAvailability? webView = null) {
        _source = source;
        _opener = opener;
        _pageContent = pageContent;
        _pageLinks = pageLinks;
        _reads = new SingleFlightReader<(string SessionId, string? Root), PlanArtifactsRead>(
            (state, ct) => _source!.ReadAsync(state.SessionId, ct),
            ex => {
                Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
                return PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable);
            },
            Apply,
            "artefacts tab",
            onSettled: RaiseLoading,
            cached: state => _source?.Cached(state.SessionId));
        _pageReads = new SingleFlightReader<string, SessionPagesRead>(
            (sessionId, ct) => _source!.ReadPagesAsync(sessionId, ct),
            ex => {
                Console.Error.WriteLine($"kcap: artefacts tab pages: {ex.Message}");
                return SessionPagesRead.Of(SessionPlansReadKind.Unreachable);
            },
            ApplyPages,
            "artefacts tab pages",
            onSettled: RaisePages,
            cached: sessionId => _source?.CachedPages(sessionId));
        _activity = activity;
        _time = time;
        _openWorkingCopy = openWorkingCopy ?? OpenFile;
        _driftReads = new SingleFlightReader<(DocumentRow Row, string? Root), DriftRead>(
            (state, ct) => Task.Run(() => new DriftRead(Drift(state.Row, state.Root)), ct),
            _ => new DriftRead(DriftState.Unknown),
            ApplyDrift,
            "document drift");
        SelectCommand = ReactiveCommand.Create<DocumentRow>(Select);
        OpenLinkCommand = ReactiveCommand.Create<string>(OpenLink);
        // In-app only where a WebView can confine the page; elsewhere the browser shows it.
        pageViewer ??= pageContent is not null && opener is not null && webView?.IsAvailable == true ? new InAppPageViewer(this)
            : opener is null ? null : new BrowserPageViewer(opener);
        _pageViewer = pageViewer;
        OpenPageCommand = ReactiveCommand.Create<PageRow>(row => _pageViewer?.Open(row.Id, row.Url));
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(Refresh), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnArtefactWritten;
        _activity.PagePublished += OnArtefactWritten;
    }

    public void SwitchSession(string? sessionId, string? root) {
        if (_tornDown || _source is null) return;
        _pendingPath = null;
        _documentsFailed = false;
        if (string.IsNullOrEmpty(sessionId)) {
            _reads.ClearCurrent(() => {
                RaiseLoading();
                Clear();
            });
            _pageReads.ClearCurrent(ResetPages);
        } else {
            _reads.Switch((sessionId, root), () => {
                RaiseLoading();
                Clear();
            });
            // Pages do not depend on the checkout, so a new root for the same session keeps them.
            if (!_pageReads.HasCurrent || _pageReads.CurrentState != sessionId) _pageReads.Switch(sessionId, ResetPages);
        }
    }

    /// Reads now, or queues one follow-up behind the read in flight.
    public void Refresh() {
        if (_tornDown) return;
        _reads.Refresh();
        _pageReads.Refresh();
    }

    public void RequestOpen() => OpenRequested?.Invoke();

    /// A link from this session: a page on the profile's server goes to the page viewer, anything
    /// else to the browser.
    public void OpenLink(string url) {
        if (_pageLinks?.PageIdFor(url) is { } id && _pageViewer is not null) _pageViewer.Open(id, url);
        else if (_opener is not null) LinkPolicy.Open(_opener, url);
    }

    /// Shows the page in the right pane, its row selected when the session lists it, and asks for
    /// the tab. Opening the page already shown reads it again only after a failed read.
    internal void OpenPage(string pageId, string browserUrl) {
        if (_tornDown || _pageContent is null || _opener is null) return;
        var row = _pages.FirstOrDefault(p => p.Id == pageId);
        if (PageViewer?.ArtefactId != pageId) {
            ClosePage();
            ClearSelection();
            foreach (var document in _documents) document.IsSelected = false;
            _viewerLifetime = new CancellationTokenSource();
            _viewerListedVersion = row?.LatestVersion ?? 0;
            PageViewer = new PageViewerViewModel(_pageContent, pageId, row?.Url ?? browserUrl, _opener, _time, webViewAvailable: true);
            LoadPage();
        } else if (PageViewer.State is PageViewerState.Unreachable or PageViewerState.SignedOut) {
            LoadPage();
        }
        foreach (var page in _pages) page.IsSelected = page.Id == pageId;
        RequestOpen();
    }

    void LoadPage() {
        if (PageViewer is not { } viewer || _viewerLifetime is not { } lifetime) return;
        PendingPageLoadForTesting = LoadSafelyAsync(viewer, lifetime.Token);
    }

    static async Task LoadSafelyAsync(PageViewerViewModel viewer, CancellationToken ct) {
        try { await viewer.LoadAsync(ct); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: artefacts tab page load: {ex.Message}"); }
    }

    void ClosePage() {
        if (PageViewer is not { } viewer) return;
        _viewerLifetime?.Cancel();
        _viewerLifetime?.Dispose();
        _viewerLifetime = null;
        viewer.Close();
        PageViewer = null;
        foreach (var page in _pages) page.IsSelected = false;
    }

    /// Selects the row whose path matches, an exact path before a suffix, and asks for the tab.
    /// Before the first read settles a miss is remembered and opened when the read lands.
    public bool OpenDocument(string path) {
        var row = Find(path);
        if (row is null) {
            if (_reads.HasCurrent && !_reads.IsCurrentSettled) _pendingPath = path;
            else return false;
            if (!HasDocuments) return false;
            RequestOpen();
            return true;
        }
        Select(row);
        RequestOpen();
        return true;
    }

    DocumentRow? Find(string path) =>
        _documents.FirstOrDefault(d => DocumentPaths.Exact(d.Path, path)) ?? _documents.FirstOrDefault(d => d.MatchesPath(path));

    void Select(DocumentRow row) {
        if (_tornDown) return;
        ClosePage();
        foreach (var document in _documents) document.IsSelected = ReferenceEquals(document, row);
        Selected = row;
        var drift = _lastDrift is { } last && last.Path == row.Path && last.Hash == row.ContentHash
            ? last.State : DriftState.Unknown;
        Reader = DocumentReaderViewModel.For(row, drift, _time.GetUtcNow());
        var root = _reads.CurrentState.Root;
        var current = _driftReads.CurrentState;
        if (_driftReads.HasCurrent && current.Root == root && current.Row.Path == row.Path
            && current.Row.ContentHash == row.ContentHash && current.Row.Source == row.Source)
            _driftReads.Refresh();
        else _driftReads.Switch((row, root));
    }

    DriftState Drift(DocumentRow row, string? root) {
        if (root is not { Length: > 0 } || row.Source != "declared") return DriftState.Unknown;
        var relative = row.Path.Replace('\\', '/');
        if (relative.Length == 0 || relative.StartsWith('/') || System.IO.Path.IsPathRooted(relative)
            || (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
            || relative.Split('/').Contains(".."))
            return DriftState.Unknown;
        var full = System.IO.Path.Combine(root, relative);
        if (!CanonicalPath.IsWithin(full, root)) return DriftState.Unknown;
        try {
            using var stream = _openWorkingCopy(full);
            if (stream is null) return DriftState.Missing;
            if (stream.Length > MaxHashedBytes) return DriftState.Unknown;
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == row.ContentHash ? DriftState.Same : DriftState.Changed;
        } catch (Exception) { return DriftState.Unknown; }
    }

    void ApplyDrift(DriftRead read) {
        // Coalesced reads can own an older row; notices follow the current reader metadata.
        if (Selected is not { } row) return;
        _lastDrift = (row.Path, row.ContentHash, read.State);
        Reader?.UpdateDrift(row, read.State);
    }

    void OnArtefactWritten() {
        if (_tornDown) return;
        Refresh();
        _settle.Change(PlanSectionViewModel.SettleDelay, Timeout.InfiniteTimeSpan);
    }

    /// Only an unreachable read is worth a retry on the poll: a signed-out or unavailable answer is
    /// what sign-in and the next plan write repair, and polling it would cost a body-carrying request
    /// per workspace per tick for nothing.
    void Apply(PlanArtifactsRead read) {
        _documentsFailed = read.Kind == SessionPlansReadKind.Unreachable;
        switch (read.Kind) {
            case SessionPlansReadKind.Ready:
                Show(read.Body?.Artifacts ?? []);
                return;
            case SessionPlansReadKind.Unreachable:
                return;
            default:
                Clear();
                return;
        }
    }

    static int KindRank(string kind) => kind switch { "plan" => 0, "spec" => 1, "design" => 2, _ => 3 };

    /// Checklists are task lists, not documents. Two entries for one text (a declaration and the
    /// transcript's reconstruction) collapse to one, the declared one first.
    void Show(IReadOnlyList<PlanArtifactDto> artifacts) {
        var rows = new List<DocumentRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in artifacts
                     .Where(a => a.Kind != "checklist")
                     .OrderBy(a => a.Source == "declared" ? 0 : 1)) {
            var pathKey = "p:" + (dto.Path ?? dto.Title);
            if (dto.ContentHash is { Length: > 0 } hash && !seen.Add("h:" + hash)) continue;
            if (!seen.Add(pathKey)) continue;
            rows.Add(DocumentRow.From(dto));
        }
        var ordered = rows.OrderBy(r => KindRank(r.Kind)).ThenBy(r => r.IsPrimary ? 0 : 1).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();

        var openPath = Selected?.Path;
        _documents.Clear();
        _documents.AddRange(ordered);
        RaiseShape();

        var reopened = openPath is null ? null : _documents.FirstOrDefault(d => d.Path == openPath);
        if (reopened is not null) Select(reopened);
        else { ClearSelection(); }

        var pending = _pendingPath;
        _pendingPath = null;
        if (pending is not null && Find(pending) is { } match) {
            Select(match);
            RequestOpen();
        }
    }

    void ClearSelection() {
        _driftReads.ClearCurrent();
        _lastDrift = null;
        Selected = null;
        Reader = null;
    }

    void Clear() {
        ClearSelection();
        if (_documents.Count == 0) return;
        _documents.Clear();
        RaiseShape();
    }

    /// An unreachable answer keeps the list it had, as the documents do; one with nothing before it
    /// says the server could not be reached rather than reading as empty.
    void ApplyPages(SessionPagesRead read) {
        _pagesFailed = read.Kind == SessionPlansReadKind.Unreachable;
        if (_pagesFailed && _pagesKind is not null) return;
        _pagesKind = read.Kind;
        var now = _time.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<PageRow> rows = read.Kind == SessionPlansReadKind.Ready
            ? read.Pages.Where(dto => dto is not null).Select(dto => PageRow.From(dto, now)).OfType<PageRow>().Where(row => seen.Add(row.Id)).ToList()
            : [];
        if (read.Kind == SessionPlansReadKind.SignedOut) PageViewer?.SignedOut();
        if (rows.Count == 0 && _pages.Count == 0) return;
        _pages.Clear();
        _pages.AddRange(rows);
        RaiseShape();
        if (PageViewer is { } viewer && _pages.FirstOrDefault(p => p.Id == viewer.ArtefactId) is { } open) {
            open.IsSelected = true;
            // A load still in flight reads the latest anyway; only a version past the one shown reloads.
            var shown = Math.Max(_viewerListedVersion, viewer.Version);
            _viewerListedVersion = Math.Max(shown, open.LatestVersion);
            if (shown > 0 && open.LatestVersion > shown) LoadPage();
        }
    }

    /// A new pages lease is a different session, so the page open for the last one closes.
    void ResetPages() {
        ClosePage();
        _pagesFailed = false;
        _pagesKind = null;
        if (_pages.Count > 0) {
            _pages.Clear();
            RaiseShape();
        }
        RaisePages();
    }

    void RaiseShape() {
        this.RaisePropertyChanged(nameof(HasDocuments));
        this.RaisePropertyChanged(nameof(HasPages));
        this.RaisePropertyChanged(nameof(HasAny));
        this.RaisePropertyChanged(nameof(EmptyReaderNote));
        this.RaisePropertyChanged(nameof(PagesNote));
        this.RaisePropertyChanged(nameof(HasPagesNote));
    }

    void RaiseLoading() {
        this.RaisePropertyChanged(nameof(IsLoading));
        this.RaisePropertyChanged(nameof(EmptyReaderNote));
    }

    void RaisePages() {
        this.RaisePropertyChanged(nameof(IsLoadingPages));
        this.RaisePropertyChanged(nameof(ShowsPagesSection));
        this.RaisePropertyChanged(nameof(PagesNote));
        this.RaisePropertyChanged(nameof(HasPagesNote));
    }

    /// Shared-read so an agent still writing the file on Windows is not refused.
    static Stream? OpenFile(string path) =>
        File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;

    public async Task TeardownAsync() {
        if (_tornDown) return;
        ClosePage();
        _tornDown = true;
        _activity.PlanWritten -= OnArtefactWritten;
        _activity.PagePublished -= OnArtefactWritten;
        _settle.Dispose();
        SelectCommand.Dispose();
        OpenLinkCommand.Dispose();
        OpenPageCommand.Dispose();
        await _reads.TeardownAsync();
        await _pageReads.TeardownAsync();
        await _driftReads.TeardownAsync(waitForPending: false);
    }
}
