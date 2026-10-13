using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Repository heading and its sessions. Streams arrive on the UI thread from the rail root.
public sealed class RailRepoViewModel : ReactiveObject, IDisposable {
    public string RootPath { get; }
    /// RootPath in a form worth showing a user, for the header tooltip.
    public string RootDisplay { get; }
    public string Label { get; }
    public bool IsNoRepository { get; }
    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public string ToggleAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Label}{(HeaderStatus is { } status ? $", {status.AccessibleName}" : "")}";
    readonly ObservableAsPropertyHelper<AgentStatusPresentation?> _headerStatus;
    public AgentStatusPresentation? HeaderStatus => _headerStatus.Value;
    readonly ObservableAsPropertyHelper<bool> _showsHeaderBadge;
    public bool ShowsHeaderBadge => _showsHeaderBadge.Value;
    readonly ObservableAsPropertyHelper<string> _tooltip;
    public string Tooltip => _tooltip.Value;

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;

    readonly ObservableCollectionExtended<RailSessionViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<RailSessionViewModel> Sessions { get; }

    readonly CompositeDisposable _disposables = new();

    public RailRepoViewModel(
            IGroup<AgentRow, string, string> group,
            IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending,
            IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>> agentsAwaitingAnswer,
            IObservable<IReadOnlyDictionary<string, int>> agentsRunningCommands,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>> pullRequests,
            IObservable<bool> localOnAppServer, Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest,
            bool isExpanded, Action<bool> setExpanded) {
        RootPath = group.Key;
        // Rows in one group share RepoGroupLabel by construction — any member names it.
        Label = group.Cache.Items[0].RepoGroupLabel;
        IsNoRepository = Label == "No repository";
        RootDisplay = DisplayFor(RootPath, Label);
        _isExpanded = isExpanded;
        var expanded = new BehaviorSubject<bool>(isExpanded).DisposeWith(_disposables);
        ToggleCommand = ReactiveCommand.Create(() => {
            IsExpanded = !IsExpanded;
            setExpanded(IsExpanded);
            expanded.OnNext(IsExpanded);
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
        }).DisposeWith(_disposables);

        // Preserve main's collapsed-group verdict, including pending permissions, questions,
        // usage limits, and background work. Expanded, the individual session marks take over.
        var header = group.Cache.Connect().QueryWhenChanged()
            .CombineLatest(agentsWithPending, agentsAwaitingAnswer, agentsRunningCommands, expanded,
                (query, pending, answering, commands, visible) => visible ? null
                    : SessionStatusDots.Rollup(query.Items, pending, answering, commands))
            .Replay(1).RefCount();
        _headerStatus = header.ToProperty(this, x => x.HeaderStatus, initialValue: null).DisposeWith(_disposables);
        _showsHeaderBadge = header.Select(status => status is not null)
            .ToProperty(this, x => x.ShowsHeaderBadge, initialValue: false).DisposeWith(_disposables);
        _tooltip = header.Select(status => status is null ? RootDisplay : $"{RootDisplay}\n\n{status.Tip}")
            .ToProperty(this, x => x.Tooltip, initialValue: RootDisplay).DisposeWith(_disposables);
        header.Subscribe(_ => this.RaisePropertyChanged(nameof(ToggleAccessibleName))).DisposeWith(_disposables);

        _countText = group.Cache.CountChanged
            .Select(c => c == 1 ? "1 session" : $"{c} sessions")
            .ToProperty(this, x => x.CountText, initialValue: "")
            .DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        group.Cache.Connect()
            .Transform(row => new RailSessionViewModel(row, selectedAgentId, agentsWithPending, remoteStale,
                openLocal, openRemote, time, agentsAwaitingAnswer, agentsRunningCommands,
                pullRequests, localOnAppServer, openPullRequest))
            .DisposeMany()
            .SortAndBind(_sessionsSource, SessionComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    static readonly IComparer<RailSessionViewModel> SessionComparer = Comparer<RailSessionViewModel>.Create((a, b) => {
        var created = a.CreatedAt.CompareTo(b.CreatedAt);
        if (created != 0) return created;
        var id = StringComparer.Ordinal.Compare(a.Id, b.Id);
        return id != 0 ? id : StringComparer.Ordinal.Compare(a.Key, b.Key);
    });

    // RootPath's own prefixes ("repo:", "path:", "daemon:{owner}/{daemon}:{path}") are a group
    // identity, not display text (RepoIdentity's own rule) — this is the one place that reads
    // them anyway, to turn the key into something worth a tooltip.
    static string DisplayFor(string rootPath, string label) {
        if (rootPath.StartsWith("repo:", StringComparison.Ordinal))
            return rootPath["repo:".Length..];
        if (rootPath.StartsWith("path:", StringComparison.Ordinal))
            return rootPath["path:".Length..];
        if (rootPath.StartsWith("daemon:", StringComparison.Ordinal)) {
            var rest = rootPath["daemon:".Length..]; // "{owner}/{daemon}:{path}"
            var pathAt = rest.IndexOf(':');
            var ownerDaemon = pathAt < 0 ? rest : rest[..pathAt];
            var path = pathAt < 0 ? "" : rest[(pathAt + 1)..];
            var daemon = ownerDaemon[(ownerDaemon.LastIndexOf('/') + 1)..];
            return path.Length == 0 ? label : $"{path} on {daemon}";
        }
        return rootPath;
    }

    public void Dispose() => _disposables.Dispose();
}
