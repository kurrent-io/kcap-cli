using System.Reactive;
using System.Reactive.Concurrency;
using System.Security.Cryptography;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The Artefacts tab: the documents a session declared or wrote, read from plan-artifacts with
/// their bodies, and the one that is open. The session id is the read's identity; a lease owns one
/// id, its root, its cancellation and its pending read, a result applies only for the current lease,
/// and every lease transition happens on the UI thread.
public sealed class ArtefactsTabViewModel : ReactiveObject {
    sealed class ReadLease(string sessionId, string? root) {
        public string SessionId { get; } = sessionId;
        public string? Root { get; } = root;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool IsReading => Pending is { IsCompleted: false };
    }

    readonly IPlanArtifactSource? _source;
    readonly PlanActivity _activity;
    readonly TimeProvider _time;
    readonly Func<string, byte[]?> _readWorkingCopy;
    readonly AvaloniaList<DocumentRow> _documents = [];
    readonly List<ReadLease> _outstanding = [];
    readonly ITimer _settle;
    ReadLease? _current;
    bool _tornDown;

    public IAvaloniaReadOnlyList<DocumentRow> Documents => _documents;
    public bool HasAny => _documents.Count > 0;
    public string SummaryText => _documents.Count == 1 ? "1 document" : $"{_documents.Count} documents";

    DocumentRow? _selected;
    public DocumentRow? Selected { get => _selected; private set => this.RaiseAndSetIfChanged(ref _selected, value); }

    DocumentReaderViewModel? _reader;
    public DocumentReaderViewModel? Reader { get => _reader; private set => this.RaiseAndSetIfChanged(ref _reader, value); }

    bool _isShown;
    /// Set by the workspace while the tab is the active one; the pane's poll refreshes only then.
    public bool IsShown { get => _isShown; set => this.RaiseAndSetIfChanged(ref _isShown, value); }

    public ReactiveCommand<DocumentRow, Unit> SelectCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    /// Something asked for the tab: a card's Open, a pane row, the pane summary.
    public event Action? OpenRequested;

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public ArtefactsTabViewModel(IPlanArtifactSource? source, PlanActivity activity, TimeProvider time, Func<string, byte[]?>? readWorkingCopy = null,
            IUrlOpener? opener = null) {
        _source = source;
        _activity = activity;
        _time = time;
        _readWorkingCopy = readWorkingCopy ?? ReadFile;
        SelectCommand = ReactiveCommand.Create<DocumentRow>(Select);
        OpenLinkCommand = ReactiveCommand.Create<string>(url => { if (opener is not null) LinkPolicy.Open(opener, url); });
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(Refresh), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnPlanWritten;
    }

    public void SwitchSession(string sessionId, string? root) {
        if (_tornDown || _source is null) return;
        var old = _current;
        _current = new ReadLease(sessionId, root);
        old?.Cts.Cancel();
        Clear();
        StartRead(_current);
    }

    /// Reads now, or queues one follow-up behind the read in flight.
    public void Refresh() {
        if (_tornDown || _current is not { } lease) return;
        if (lease.IsReading) lease.RefreshPending = true;
        else StartRead(lease);
    }

    public void RequestOpen() => OpenRequested?.Invoke();

    /// Selects the row whose path matches and asks for the tab; false when no row does.
    public bool OpenDocument(string path) {
        var row = _documents.FirstOrDefault(d => d.MatchesPath(path));
        if (row is null) return false;
        Select(row);
        RequestOpen();
        return true;
    }

    void Select(DocumentRow row) {
        foreach (var document in _documents) document.IsSelected = ReferenceEquals(document, row);
        Selected = row;
        Reader = DocumentReaderViewModel.For(row, Drift(row), _time.GetUtcNow());
    }

    DriftState Drift(DocumentRow row) {
        if (_current?.Root is not { Length: > 0 } root || row.Source != "declared") return DriftState.Unknown;
        var full = System.IO.Path.Combine(root, row.Path.Replace('\\', '/'));
        byte[]? bytes;
        try { bytes = _readWorkingCopy(full); } catch (Exception) { return DriftState.Unknown; }
        if (bytes is null) return DriftState.Missing;
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) == row.ContentHash ? DriftState.Same : DriftState.Changed;
    }

    void OnPlanWritten() {
        if (_tornDown) return;
        Refresh();
        _settle.Change(PlanSectionViewModel.SettleDelay, Timeout.InfiniteTimeSpan);
    }

    void StartRead(ReadLease lease) {
        lease.RefreshPending = false;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
    }

    async Task RunReadAsync(ReadLease lease) {
        PlanArtifactsRead? read = null;
        try {
            read = await _source!.ReadAsync(lease.SessionId, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
            read = PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable);
        }
        try {
            await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, read));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
        }
    }

    void Settle(ReadLease lease, PlanArtifactsRead? read) {
        _outstanding.Remove(lease);
        var current = ReferenceEquals(lease, _current) && !_tornDown;
        if (!current) { lease.Cts.Dispose(); return; }
        try {
            if (read is not null) Apply(read);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: artefacts tab: {ex.Message}");
        }
        if (lease.RefreshPending) StartRead(lease);
    }

    void Apply(PlanArtifactsRead read) {
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
            var key = dto.ContentHash is { Length: > 0 } hash ? "h:" + hash : "p:" + (dto.Path ?? dto.Title);
            if (!seen.Add(key) || !seen.Add("p:" + (dto.Path ?? dto.Title))) continue;
            rows.Add(DocumentRow.From(dto));
        }
        var ordered = rows.OrderBy(r => KindRank(r.Kind)).ThenBy(r => r.IsPrimary ? 0 : 1).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();

        var openPath = Selected?.Path;
        _documents.Clear();
        _documents.AddRange(ordered);
        RaiseShape();

        var reopened = openPath is null ? null : _documents.FirstOrDefault(d => d.Path == openPath);
        if (reopened is not null) Select(reopened);
        else { Selected = null; Reader = null; }
    }

    void Clear() {
        Selected = null;
        Reader = null;
        if (_documents.Count == 0) return;
        _documents.Clear();
        RaiseShape();
    }

    void RaiseShape() {
        this.RaisePropertyChanged(nameof(HasAny));
        this.RaisePropertyChanged(nameof(SummaryText));
    }

    /// Shared-read so an agent still writing the file on Windows is not refused.
    static byte[]? ReadFile(string path) {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _activity.PlanWritten -= OnPlanWritten;
        _settle.Dispose();
        SelectCommand.Dispose();
        OpenLinkCommand.Dispose();
        var leases = _outstanding.ToArray();
        foreach (var lease in leases) lease.Cts.Cancel();
        _current = null;
        foreach (var lease in leases)
            if (lease.Pending is { } pending) {
                try { await pending; } catch (Exception) { }
            }
        foreach (var lease in leases) lease.Cts.Dispose();
    }
}
