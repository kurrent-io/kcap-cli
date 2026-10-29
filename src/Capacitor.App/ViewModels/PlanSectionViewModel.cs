using System.Reactive;
using System.Reactive.Concurrency;
using Avalonia.Collections;
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

    readonly IPlanSource? _source;
    readonly SingleFlightReader<string, SessionPlansRead> _reader;
    readonly PlanActivity _activity;
    readonly AvaloniaList<PlanTaskRow> _tasks = [];
    readonly AvaloniaList<PlanTaskRow> _inProgress = [];
    readonly ITimer _settle;
    IReadOnlyList<PlanTaskCount> _counts = [];
    bool _tornDown;

    public IAvaloniaReadOnlyList<PlanTaskRow> Tasks => _tasks;
    /// What the collapsed section lists: the rows of Tasks that are in progress, in plan order.
    public IAvaloniaReadOnlyList<PlanTaskRow> InProgressTasks => _inProgress;

    public bool HasPlan       => HasTasks;
    /// Keeps the section chrome while a session switch's first read is in flight.
    public bool IsLoading => _reader.IsLoading;
    public bool ShowsPlan => HasPlan || IsLoading;
    public bool HasTasks      => _tasks.Count > 0;
    public bool HasInProgress => _inProgress.Count > 0;
    public int DoneCount => _tasks.Count(task => task.IsSettled);
    /// What the expanded header says; collapsed, the header shows Counts.
    public string HeaderText => $"{DoneCount} of {_tasks.Count} done";
    /// Pending, in progress, done: one entry per state something is in, so the numbers add up
    /// to the list.
    public IReadOnlyList<PlanTaskCount> Counts => _counts;

    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; private set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _reader.PendingReadForTesting;

    public PlanSectionViewModel(IPlanSource? source, PlanActivity activity, TimeProvider time) {
        _source = source;
        _reader = new SingleFlightReader<string, SessionPlansRead>(
            (id, ct) => _source!.ReadAsync(id, ct),
            ex => {
                Console.Error.WriteLine($"kcap: plan section: {ex.Message}");
                return SessionPlansRead.Of(SessionPlansReadKind.Unreachable);
            },
            Apply,
            "plan section",
            onSettled: RaiseLoading,
            cached: id => _source?.Cached(id));
        _activity = activity;
        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
        _settle = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(Refresh), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.PlanWritten += OnPlanWritten;
        _activity.SessionOverChanged += OnSessionOverChanged;
    }

    public void SwitchSession(string? sessionId) {
        if (_tornDown || _source is null) return;
        if (sessionId is null) {
            _reader.ClearCurrent();
            Clear();
            RaiseLoading();
            return;
        }
        _reader.Switch(sessionId, () => {
            RaiseLoading();
            Clear();
        });
    }

    /// Reads now, or queues one follow-up behind the read in flight.
    public void Refresh() {
        if (!_tornDown) _reader.Refresh();
    }

    void OnPlanWritten() {
        if (_tornDown) return;
        Refresh();
        _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
    }

    void OnSessionOverChanged() {
        foreach (var task in _tasks) task.Present(_activity.SessionOver);
        SyncCounts();
    }

    void Apply(SessionPlansRead read) {
        switch (read.Kind) {
            case SessionPlansReadKind.Ready:
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
        StableRows.Sync(_tasks, RowsFor(tasks));
        for (var i = 0; i < tasks.Count; i++) _tasks[i].Present(tasks[i], _activity.SessionOver);

        RaiseShape();
    }

    /// A task the plan still names keeps its row, whatever was added, dropped or moved around it.
    List<PlanTaskRow> RowsFor(List<PlanLedgerTaskDto> tasks) {
        var known = new Dictionary<string, PlanTaskRow>(StringComparer.Ordinal);
        foreach (var row in _tasks) known.TryAdd(row.TaskId, row);
        return [.. tasks.Select(task => known.Remove(RowKey(task), out var row) ? row : new PlanTaskRow(RowKey(task)))];
    }

    /// A task the server sent without an id is still one row per position.
    static string RowKey(PlanLedgerTaskDto task) => task.TaskId is { Length: > 0 } id ? id : $"#{task.Ordinal}";

    void Clear() {
        if (_tasks.Count == 0) return;
        _tasks.Clear();
        RaiseShape();
    }

    void RaiseShape() {
        StableRows.Sync(_inProgress, _tasks.Where(task => task.IsInProgress));
        SyncCounts();
        this.RaisePropertyChanged(nameof(HasPlan));
        this.RaisePropertyChanged(nameof(ShowsPlan));
        this.RaisePropertyChanged(nameof(HasTasks));
        this.RaisePropertyChanged(nameof(HasInProgress));
        this.RaisePropertyChanged(nameof(DoneCount));
        this.RaisePropertyChanged(nameof(HeaderText));
    }

    void SyncCounts() {
        var counts = CountStates();
        if (!counts.SequenceEqual(_counts)) {
            _counts = counts;
            this.RaisePropertyChanged(nameof(Counts));
        }
    }

    void RaiseLoading() {
        this.RaisePropertyChanged(nameof(IsLoading));
        this.RaisePropertyChanged(nameof(ShowsPlan));
    }

    PlanTaskCount[] CountStates() {
        var done = DoneCount;
        PlanTaskCount[] states = [
            new(PlanTaskState.Pending, _tasks.Count - _inProgress.Count - done),
            new(PlanTaskState.InProgress, _inProgress.Count, _inProgress.Count > 0 && !_activity.SessionOver),
            new(PlanTaskState.Completed, done),
        ];
        return [.. states.Where(state => state.Count > 0)];
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _activity.PlanWritten -= OnPlanWritten;
        _activity.SessionOverChanged -= OnSessionOverChanged;
        _settle.Dispose();
        ToggleCommand.Dispose();
        await _reader.TeardownAsync();
    }
}
