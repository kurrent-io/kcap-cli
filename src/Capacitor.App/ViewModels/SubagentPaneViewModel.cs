using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent's own transcript, shown in place of its session's chat until the user goes back. It
/// reads through its own feed and run rows, so the parent's are untouched while it is open. A reset of
/// the parent's feed rebuilds its rows, so the pane follows its row by call id rather than by reference.
public sealed class SubagentPaneViewModel : ReactiveObject {
    internal const string Notice = "this is a subagent's own transcript";

    readonly SessionRuns _runs;
    readonly BehaviorSubject<bool> _running;
    readonly string? _agentId;
    readonly Action _back;
    RunRow _row;
    IDisposable? _rowWatch;

    public SubagentPaneViewModel(
            SessionRuns runs, RunRow row, string parentTitle, AgentOrigin origin, string parentAgentId, string vendor,
            string feedKey, Func<string, IChatTranscriptFeed> openFeed, IUrlOpener opener, TimeProvider time,
            IPermissionService permissions, Action back) {
        _runs       = runs;
        _row        = row;
        _agentId    = row.AgentId;
        _back       = back;
        _running    = new BehaviorSubject<bool>(row.IsRunning);
        ParentTitle = parentTitle;
        Watch(row);
        _runs.Rows.CollectionChanged += OnRowsChanged;
        var session = _running.DistinctUntilChanged()
            .Select(running => new ChatSessionInfo(
                running ? "Running" : "Exited", running ? "Running" : "Finished", vendor, null, null, running ? false : null, !running, Notice, feedKey));
        // Its own key keeps the parent's pending cards, which are keyed by the parent, out of this pane.
        Chat = new ChatTabViewModel(
            $"{parentAgentId}/{row.AgentId}", origin, session, Observable.Return<string[]?>(null), new ReadOnlyChatInput(),
            new NoAttachmentUploader(), openFeed, opener, time, permissions, new SessionRuns(time));
        BackCommand = ReactiveCommand.Create(back);
    }

    public RunRow Row {
        get => _row;
        private set => this.RaiseAndSetIfChanged(ref _row, value);
    }

    /// Readable from any thread: the remote feed asks it from the chat's worker.
    public bool IsRunning => _running.Value;

    public string ParentTitle { get; }
    public ChatTabViewModel Chat { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }

    void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (_runs.Rows.FirstOrDefault(r => r.CallId == _row.CallId) is { } current && !ReferenceEquals(current, _row)) {
            Row = current;
            Watch(current);
        }
    }

    // The chat reads the transcript of the agent the pane opened on; a row now naming another agent
    // closes the pane rather than show one agent's state over another's transcript. A rebuilt row is
    // unnamed until its id is replayed, which is not another agent.
    void Watch(RunRow row) {
        _rowWatch?.Dispose();
        _rowWatch = row.WhenAnyValue(r => r.IsRunning, r => r.AgentId, (running, agentId) => (Running: running, AgentId: agentId))
            .Subscribe(state => {
                if (state.AgentId is not null && state.AgentId != _agentId) _back();
                else _running.OnNext(state.Running);
            });
    }

    public Task TeardownAsync() {
        _runs.Rows.CollectionChanged -= OnRowsChanged;
        _rowWatch?.Dispose();
        BackCommand.Dispose();
        return Chat.TeardownAsync();
    }
}
