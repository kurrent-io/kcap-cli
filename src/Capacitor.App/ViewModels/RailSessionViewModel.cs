using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per row revision (DynamicData Transform), so every
/// static field is computed once from the ctor row. IsSelected and Status stay live because
/// selection and pending-set membership each change without a row revision. Age is a
/// point-in-time snapshot.
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    public string Key { get; }
    public string? CheckoutPath { get; }
    /// Null when the row has no title, in which case the chips line stands alone as the row.
    public string? Primary { get; }
    public bool HasTitle { get; }
    public string SessionAccessibleName => Primary ?? (HasVendor ? Vendor : "Session");
    public string Vendor { get; }
    public bool HasVendor { get; }
    public string? Model { get; }
    public string Meta { get; }
    public string Tooltip => MachineBadge is { Length: > 0 } machine
        ? $"{Status.Tip}\n\n{machine}\nMachine · remote daemon" : Status.Tip;
    /// The daemon name badge for a remote row; null for a local one.
    public string? MachineBadge { get; }
    public string MachineLabel => $"on {MachineBadge}";
    public string MachineTooltip => $"This session runs on the remote daemon ‘{MachineBadge}’.";
    public bool IsRemote { get; }
    /// A launch the daemon has not published yet: Meta carries its stage instead of an age.
    public bool IsStarting { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPullRequestCommand { get; }
    RailPullRequestSummary? _pullRequest;
    public RailPullRequestSummary? PullRequest => _pullRequest;
    public string PullRequestLabel => PullRequest?.Label ?? "";
    public PullRequestStatus? PullRequestStatus => PullRequest?.Status;
    public bool HasPullRequestLifecycle => PullRequest?.HasLifecycle == true;
    public string PullRequestTooltip => $"{PullRequest?.Description}\n\nLinked to this session: {SessionAccessibleName}\nOpen pull request details";
    public bool HasPullRequest => PullRequest is not null;
    public string PullRequestAccessibleName => $"Open {PullRequestLabel} for {Primary ?? Vendor}";
    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<AgentStatusPresentation> _status;
    public AgentStatusPresentation Status => _status.Value;

    readonly ObservableAsPropertyHelper<bool> _isStale;
    /// A remote row greys out while the lane is stale; a local row is never stale.
    public bool IsStale => _isStale.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedAgentId,
            IObservable<IReadOnlySet<string>> agentsWithPending, IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null,
            IObservable<IReadOnlyDictionary<string, int>>? agentsRunningCommands = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequests = null,
            IObservable<bool>? localOnAppServer = null, Action<string, AgentOrigin>? openPullRequest = null) {
        Id = row.Id;
        Key = row.Key;
        CheckoutPath = (row.WorkLocation == WorkLocationText.Borrowed ? row.BorrowedFrom : row.WorktreePath) ?? row.RepoPath;
        (pullRequests ?? Observable.Return<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                FrozenDictionary<string, RailPullRequestSummary>.Empty))
            .CombineLatest(localOnAppServer ?? Observable.Return(true), (map, localReadable) =>
                row.SessionId is { Length: > 0 } sid && (row.Origin != AgentOrigin.Local || localReadable)
                    ? map.GetValueOrDefault(sid) : null)
            .DistinctUntilChanged()
            .Subscribe(summary => {
                this.RaiseAndSetIfChanged(ref _pullRequest, summary, nameof(PullRequest));
                this.RaisePropertyChanged(nameof(PullRequestLabel));
                this.RaisePropertyChanged(nameof(PullRequestStatus));
                this.RaisePropertyChanged(nameof(HasPullRequestLifecycle));
                this.RaisePropertyChanged(nameof(PullRequestTooltip));
                this.RaisePropertyChanged(nameof(HasPullRequest));
                this.RaisePropertyChanged(nameof(PullRequestAccessibleName));
            }).DisposeWith(_disposables);
        var kindExtra = row.Kind == "agent" ? null : row.Kind;
        var borrowed = row.WorkLocation == WorkLocationText.Borrowed ? "borrowed" : null;
        var age = UptimeFormat.Format(
            time.GetUtcNow().UtcDateTime - DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));

        Primary = string.IsNullOrEmpty(row.Title) ? null : row.Title;
        HasTitle = Primary is not null;
        Vendor = row.Vendor;
        HasVendor = !string.IsNullOrEmpty(row.Vendor);
        Model = string.IsNullOrEmpty(row.Model) ? null : row.Model;
        IsStarting = row.Origin == AgentOrigin.Pending;
        var subagents = row.LiveSubagents is int live and > 0 ? $"{live} subagent{(live == 1 ? "" : "s")}" : null;
        Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : Join(kindExtra, borrowed, age, subagents);
        MachineBadge = row.MachineBadge;
        IsRemote = row.Origin == AgentOrigin.Remote;

        _isSelected = selectedAgentId.Select(sel => sel == row.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var answering = agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty);
        var commands = agentsRunningCommands ?? Observable.Return<IReadOnlyDictionary<string, int>>(FrozenDictionary<string, int>.Empty);
        _status = agentsWithPending.CombineLatest(answering, commands,
                (pending, asked, running) => SessionStatusDots.ForRow(row, pending.Contains(row.Id), asked.Contains(row.Id), running.GetValueOrDefault(row.Key)))
            .ToProperty(this, x => x.Status, initialValue: SessionStatusDots.ForRow(row, pending: false))
            .DisposeWith(_disposables);

        _isStale = (IsRemote ? remoteStale : Observable.Return(false))
            .ToProperty(this, x => x.IsStale, initialValue: false)
            .DisposeWith(_disposables);

        OpenCommand = ReactiveCommand.Create(() => (IsRemote ? openRemote : openLocal)(row.Id));
        _disposables.Add(OpenCommand);
        OpenPullRequestCommand = ReactiveCommand.Create(() => {
            if (HasPullRequest) openPullRequest?.Invoke(row.Id, row.Origin == AgentOrigin.Remote ? AgentOrigin.Remote : AgentOrigin.Local);
        });
        _disposables.Add(OpenPullRequestCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
