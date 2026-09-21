using System.Reactive;
using System.Reactive.Concurrency;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Plans;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The pane's PLAN section: the plan the session works from, read from the server. The pane owns
/// which session is shown and when to poll; this owns the read and the rows.
///
/// The session id is the read's identity. A lease owns one id, its cancellation and its pending
/// read; a result applies only for the current lease, every lease is kept until its read settles
/// so teardown can await them all, and every lease transition happens on the UI thread.
public sealed class PlanSectionViewModel : ReactiveObject {
    /// The server answers a read from a projection that can trail the write that prompted it, so a
    /// write is read twice: at once, and again after this long.
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    sealed class ReadLease(string sessionId) {
        public string SessionId { get; } = sessionId;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool IsReading => Pending is { IsCompleted: false };
    }

    readonly IPlanSource? _source;
    readonly PlanActivity _activity;
    readonly AvaloniaList<PlanTaskRow> _tasks = [];
    readonly AvaloniaList<PlanDocumentRow> _documents = [];
    readonly List<ReadLease> _outstanding = [];
    readonly ITimer _settle;
    ReadLease? _current;
    bool _tornDown;

    public IAvaloniaReadOnlyList<PlanTaskRow> Tasks => _tasks;
    public IAvaloniaReadOnlyList<PlanDocumentRow> Documents => _documents;

    public bool HasPlan      => HasTasks || HasDocuments;
    public bool HasTasks     => _tasks.Count > 0;
    public bool HasDocuments => _documents.Count > 0;
    public int DoneCount => _tasks.Count(task => task.IsSettled);
    public int OpenCount => _tasks.Count - DoneCount;
    /// What the expanded header says; folded, the header shows the two counts beside their marks.
    public string HeaderText => $"{DoneCount} of {_tasks.Count} done";
    public string CountsTip => $"{DoneCount} done · {OpenCount} open";

    bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public PlanSectionViewModel(IPlanSource? source, PlanActivity activity, TimeProvider time) {
        _source = source;
        _activity = activity;
        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(Refresh), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnPlanWritten;
        _activity.SessionOverChanged += OnSessionOverChanged;
    }

    public void SwitchSession(string sessionId) {
        if (_tornDown || _source is null) return;
        var old = _current;
        _current = new ReadLease(sessionId);
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

    void OnPlanWritten() {
        if (_tornDown) return;
        Refresh();
        _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
    }

    void OnSessionOverChanged() {
        foreach (var task in _tasks) task.Present(_activity.SessionOver);
    }

    void StartRead(ReadLease lease) {
        lease.RefreshPending = false;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
    }

    async Task RunReadAsync(ReadLease lease) {
        SessionPlansRead? read = null;
        try {
            read = await _source!.ReadAsync(lease.SessionId, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: plan section: {ex.Message}");
            read = SessionPlansRead.Of(SessionPlansReadKind.Unreachable);
        }
        try {
            await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, read));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: plan section: {ex.Message}");
        }
    }

    void Settle(ReadLease lease, SessionPlansRead? read) {
        _outstanding.Remove(lease);
        var current = ReferenceEquals(lease, _current) && !_tornDown;
        if (!current) { lease.Cts.Dispose(); return; }
        try {
            if (read is not null) Apply(read);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: plan section: {ex.Message}");
        }
        if (lease.RefreshPending) StartRead(lease);
    }

    void Apply(SessionPlansRead read) {
        switch (read.Kind) {
            case SessionPlansReadKind.Ready:
                // The server lists most recently touched first, which stands in for a continued
                // session that has not written to its predecessor's plan yet.
                Show(read.Plans.FirstOrDefault(plan => plan.IsCurrent) ?? (read.Plans.Count > 0 ? read.Plans[0] : null));
                return;
            case SessionPlansReadKind.Unreachable:
                return;
            default:
                Clear();
                return;
        }
    }

    void Show(SessionPlanDto? plan) {
        if (plan is null) { Clear(); return; }

        var tasks = plan.Tasks.OrderBy(task => task.Ordinal).ToList();
        var sameRows = tasks.Count == _tasks.Count && tasks.Zip(_tasks).All(pair => RowKey(pair.First) == pair.Second.TaskId);
        if (!sameRows) {
            _tasks.Clear();
            _tasks.AddRange(tasks.Select(task => new PlanTaskRow(RowKey(task))));
        }
        for (var i = 0; i < tasks.Count; i++) _tasks[i].Present(tasks[i], _activity.SessionOver);

        var documents = plan.Documents
            .OrderBy(document => KindRank(document.Kind))
            .Select(document => new PlanDocumentRow(document.Kind, document.Path))
            .ToList();
        if (!documents.SequenceEqual(_documents)) {
            _documents.Clear();
            _documents.AddRange(documents);
        }
        RaiseShape();
    }

    /// A task the server sent without an id is still one row per position.
    static string RowKey(PlanLedgerTaskDto task) => task.TaskId is { Length: > 0 } id ? id : $"#{task.Ordinal}";

    static int KindRank(string kind) => kind switch {
        "plan"   => 0,
        "spec"   => 1,
        "design" => 2,
        _        => 3,
    };

    void Clear() {
        if (_tasks.Count == 0 && _documents.Count == 0) return;
        _tasks.Clear();
        _documents.Clear();
        RaiseShape();
    }

    void RaiseShape() {
        this.RaisePropertyChanged(nameof(HasPlan));
        this.RaisePropertyChanged(nameof(HasTasks));
        this.RaisePropertyChanged(nameof(HasDocuments));
        this.RaisePropertyChanged(nameof(DoneCount));
        this.RaisePropertyChanged(nameof(OpenCount));
        this.RaisePropertyChanged(nameof(HeaderText));
        this.RaisePropertyChanged(nameof(CountsTip));
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _activity.PlanWritten -= OnPlanWritten;
        _activity.SessionOverChanged -= OnSessionOverChanged;
        _settle.Dispose();
        ToggleCommand.Dispose();
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
