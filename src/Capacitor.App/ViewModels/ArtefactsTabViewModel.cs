using System.Reactive;
using System.Reactive.Concurrency;
using System.Security.Cryptography;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The Artefacts tab: the documents a session declared or wrote, read from plan-artifacts with
/// their bodies, the one that is open, and the pages the session published. The session id is
/// each read's identity; a lease owns one id, its root, its cancellation and its pending read, a
/// result applies only for the current lease, and every lease transition happens on the UI thread.
public sealed class ArtefactsTabViewModel : ReactiveObject {
    readonly IPlanArtifactSource? _source;
    readonly SingleFlightReader<(string SessionId, string? Root), PlanArtifactsRead> _reads;
    readonly IPageSource? _pageSource;
    readonly SingleFlightReader<string, SessionPagesRead> _pageReads;
    sealed record DriftRead(DriftState State);
    readonly SingleFlightReader<(DocumentRow Row, string? Root), DriftRead> _driftReads;
    readonly PlanActivity _activity;
    readonly TimeProvider _time;
    const long MaxHashedBytes = 4 * 1024 * 1024;

    readonly Func<string, Stream?> _openWorkingCopy;
    readonly AvaloniaList<DocumentRow> _documents = [];
    readonly AvaloniaList<PageRow> _pages = [];
    SessionPlansReadKind? _pagesKind;
    readonly ITimer _settle;
    bool _tornDown;
    string? _pendingPath;

    public IAvaloniaReadOnlyList<DocumentRow> Documents => _documents;
    public IAvaloniaReadOnlyList<PageRow> Pages => _pages;
    /// The last applied documents read was unreachable, so the pane's poll retries it even with the
    /// tab hidden. Pages stay out of it: they are read on every tick already.
    internal bool LastReadFailed { get; private set; }
    public bool HasAny => _documents.Count > 0;
    public bool HasPages => _pages.Count > 0;
    public bool HasAnyArtefacts => HasAny || HasPages;
    /// True while either read has a lease open that has not yet applied a result — distinct from a settled empty list.
    public bool IsLoading => _reads.IsLoading || _pageReads.IsLoading;
    /// Right-pane prompt when nothing is open: loading, settled empty, or pick a row.
    public string EmptyReaderNote => _reads.IsLoading ? "Loading documents…"
        : HasPages ? "Select a document or page."
        : HasAny ? "Select a document to read it."
        : "No documents yet.";
    /// Beside the page list when it has no rows to show; null when it does.
    public string? PagesNote => HasPages ? null
        : _pageReads.IsLoading ? "Loading pages…"
        : _pagesKind switch {
            SessionPlansReadKind.Unavailable => "Pages unavailable on this server.",
            SessionPlansReadKind.SignedOut => "Sign in to see pages.",
            SessionPlansReadKind.Unreachable => "Couldn't reach the server for pages.",
            _ => "No pages yet.",
        };

    (string Path, string? Hash, DriftState State)? _lastDrift;
    DocumentRow? _selected;
    public DocumentRow? Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }

    DocumentReaderViewModel? _reader;
    public DocumentReaderViewModel? Reader { get => _reader; private set => this.RaiseAndSetIfChanged(ref _reader, value); }

    bool _isShown;
    /// Set by the workspace while the tab is the active one; the pane's poll refreshes only then.
    public bool IsShown { get => _isShown; set => this.RaiseAndSetIfChanged(ref _isShown, value); }

    public ReactiveCommand<DocumentRow, Unit> SelectCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<PageRow, Unit> OpenPageCommand { get; }
    /// Something asked for the tab: a card's Open, a pane row, the pane summary.
    public event Action? OpenRequested;
    /// A page row was opened; with no subscriber the page opens in the browser.
    public event Action<PageRow>? PageOpenRequested;

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _reads.PendingReadForTesting;
    internal Task? PendingDriftForTesting => _driftReads.PendingReadForTesting;
    internal Task? PendingPagesReadForTesting => _pageReads.PendingReadForTesting;

    public ArtefactsTabViewModel(IPlanArtifactSource? source, PlanActivity activity, TimeProvider time, Func<string, Stream?>? openWorkingCopy = null,
            IPageSource? pages = null, IUrlOpener? opener = null) {
        _source = source;
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
        _pageSource = pages;
        _pageReads = new SingleFlightReader<string, SessionPagesRead>(
            (sessionId, ct) => _pageSource!.ReadAsync(sessionId, ct),
            ex => {
                Console.Error.WriteLine($"kcap: artefacts tab pages: {ex.Message}");
                return SessionPagesRead.Of(SessionPlansReadKind.Unreachable);
            },
            ApplyPages,
            "artefacts tab pages",
            onSettled: RaiseLoading,
            cached: sessionId => _pageSource?.Cached(sessionId));
        _activity = activity;
        _time = time;
        _openWorkingCopy = openWorkingCopy ?? OpenFile;
        _driftReads = new SingleFlightReader<(DocumentRow Row, string? Root), DriftRead>(
            (state, ct) => Task.Run(() => new DriftRead(Drift(state.Row, state.Root)), ct),
            _ => new DriftRead(DriftState.Unknown),
            ApplyDrift,
            "document drift");
        SelectCommand = ReactiveCommand.Create<DocumentRow>(Select);
        OpenLinkCommand = ReactiveCommand.Create<string>(url => { if (opener is not null) LinkPolicy.Open(opener, url); });
        OpenPageCommand = ReactiveCommand.Create<PageRow>(row => {
            if (PageOpenRequested is { } viewer) viewer(row);
            else if (opener is not null) LinkPolicy.Open(opener, row.Url);
        });
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(RefreshDocuments), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnPlanWritten;
        _activity.PagePublished += OnPagePublished;
    }

    public void SwitchSession(string? sessionId, string? root) {
        if (_tornDown) return;
        if (_source is not null) {
            _pendingPath = null;
            LastReadFailed = false;
            if (string.IsNullOrEmpty(sessionId)) {
                _reads.ClearCurrent(() => {
                    RaiseLoading();
                    Clear();
                });
            } else {
                _reads.Switch((sessionId, root), () => {
                    RaiseLoading();
                    Clear();
                });
            }
        }
        if (_pageSource is not null) {
            _pagesKind = null;
            if (string.IsNullOrEmpty(sessionId)) {
                _pageReads.ClearCurrent(() => {
                    RaiseLoading();
                    ClearPages();
                });
            } else {
                _pageReads.Switch(sessionId, () => {
                    RaiseLoading();
                    ClearPages();
                });
            }
        }
    }

    /// Reads both lists now, or queues one follow-up behind each read in flight.
    public void Refresh() {
        RefreshDocuments();
        RefreshPages();
    }

    /// The documents read alone: it carries bodies, so the pane polls it only while it is worth it.
    internal void RefreshDocuments() {
        if (!_tornDown) _reads.Refresh();
    }

    /// The pages read alone: it carries no bodies, so the pane polls it whether or not the tab is shown.
    public void RefreshPages() {
        if (!_tornDown) _pageReads.Refresh();
    }

    /// The pane's per-tick pages read. A server without the list route answers the same on every
    /// tick, so it is asked again only by a publish, a sign-in or a session switch.
    internal void PollPages() {
        if (_pagesKind != SessionPlansReadKind.Unavailable) RefreshPages();
    }

    public void RequestOpen() => OpenRequested?.Invoke();

    /// Selects the row whose path matches, an exact path before a suffix, and asks for the tab.
    /// Before the first read settles a miss is remembered and opened when the read lands.
    public bool OpenDocument(string path) {
        var row = Find(path);
        if (row is null) {
            if (_reads.HasCurrent && !_reads.IsCurrentSettled) _pendingPath = path;
            else return false;
            if (!HasAny) return false;
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

    void OnPlanWritten() {
        if (_tornDown) return;
        RefreshDocuments();
        _settle.Change(PlanSectionViewModel.SettleDelay, Timeout.InfiniteTimeSpan);
    }

    void OnPagePublished() => RefreshPages();

    /// Only an unreachable read is worth a retry on the poll: a signed-out or unavailable answer is
    /// what sign-in and the next plan write repair, and polling it would cost a body-carrying request
    /// per workspace per tick for nothing.
    void Apply(PlanArtifactsRead read) {
        LastReadFailed = read.Kind == SessionPlansReadKind.Unreachable;
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

    /// An unreachable read keeps the rows it has; any other non-Ready answer clears them.
    void ApplyPages(SessionPagesRead read) {
        _pagesKind = read.Kind;
        switch (read.Kind) {
            case SessionPlansReadKind.Ready:
                ShowPages(read.Pages);
                break;
            case SessionPlansReadKind.Unreachable:
                break;
            default:
                ClearPages();
                break;
        }
        this.RaisePropertyChanged(nameof(PagesNote));
    }

    /// The list is polled every tick, so an unchanged read leaves the rows, and the view, alone.
    void ShowPages(IReadOnlyList<ArtefactDto> pages) {
        var ordered = pages.OrderByDescending(p => p.UpdatedAt).ToList();
        if (ordered.Count == _pages.Count && ordered.Zip(_pages).All(pair =>
                pair.First.ArtefactId == pair.Second.ArtefactId && pair.First.LatestVersion == pair.Second.LatestVersion
             && pair.First.UpdatedAt == pair.Second.UpdatedAt))
            return;
        var now = _time.GetUtcNow();
        _pages.Clear();
        _pages.AddRange(ordered.Select(p => PageRow.From(p, now)));
        RaisePagesShape();
    }

    void ClearPages() {
        if (_pages.Count == 0) return;
        _pages.Clear();
        RaisePagesShape();
    }

    void RaisePagesShape() {
        this.RaisePropertyChanged(nameof(HasPages));
        this.RaisePropertyChanged(nameof(HasAnyArtefacts));
        this.RaisePropertyChanged(nameof(PagesNote));
        this.RaisePropertyChanged(nameof(EmptyReaderNote));
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

    void RaiseShape() {
        this.RaisePropertyChanged(nameof(HasAny));
        this.RaisePropertyChanged(nameof(HasAnyArtefacts));
        this.RaisePropertyChanged(nameof(EmptyReaderNote));
    }

    void RaiseLoading() {
        this.RaisePropertyChanged(nameof(IsLoading));
        this.RaisePropertyChanged(nameof(EmptyReaderNote));
        this.RaisePropertyChanged(nameof(PagesNote));
    }

    /// Shared-read so an agent still writing the file on Windows is not refused.
    static Stream? OpenFile(string path) =>
        File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _activity.PlanWritten -= OnPlanWritten;
        _activity.PagePublished -= OnPagePublished;
        _settle.Dispose();
        SelectCommand.Dispose();
        OpenLinkCommand.Dispose();
        OpenPageCommand.Dispose();
        await _reads.TeardownAsync();
        await _pageReads.TeardownAsync();
        await _driftReads.TeardownAsync(waitForPending: false);
    }
}
