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
    readonly BehaviorSubject<bool> _expanded;
    readonly Action<bool> _setExpanded;

    public RailRepoViewModel(
            IGroup<AgentRow, string, string> group,
            IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending,
            IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>> agentsAwaitingAnswer,
            IObservable<IReadOnlyDictionary<string, int>> agentsRunningCommands,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>> pullRequests,
            IObservable<bool> localOnAppServer, Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest,
            bool isExpanded, Action<bool> setExpanded, IObservable<Unit>? ageTicks = null) {
        RootPath = group.Key;
        // Rows in one group share RepoGroupLabel by construction — any member names it.
        Label = group.Cache.Items[0].RepoGroupLabel;
        IsNoRepository = Label == "No repository";
        RootDisplay = DisplayFor(RootPath, Label);
        _isExpanded = isExpanded;
        _setExpanded = setExpanded;
        _expanded = new BehaviorSubject<bool>(isExpanded).DisposeWith(_disposables);
        ToggleCommand = ReactiveCommand.Create(() => SetExpanded(!IsExpanded)).DisposeWith(_disposables);

        var query = group.Cache.Connect().QueryWhenChanged().Replay(1).RefCount();
        var pendingHere = query.CombineLatest(agentsWithPending, (rows, pending) =>
                rows.Items.Where(row => pending.Contains(row.Id)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<HashSet<string>>.Create((a, b) => a!.SetEquals(b!)));
        var answeringHere = query.CombineLatest(agentsAwaitingAnswer, (rows, answering) =>
                rows.Items.Where(row => answering.Contains(row.Id)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<HashSet<string>>.Create((a, b) => a!.SetEquals(b!)));
        var commandsHere = query.CombineLatest(agentsRunningCommands, (rows, commands) =>
                rows.Items.Where(row => commands.GetValueOrDefault(row.Key) > 0)
                    .ToDictionary(row => row.Key, row => commands[row.Key], StringComparer.Ordinal))
            .DistinctUntilChanged(EqualityComparer<Dictionary<string, int>>.Create((a, b) => a!.Count == b!.Count && a.All(item => b.GetValueOrDefault(item.Key) == item.Value)));
        var header = query.CombineLatest(pendingHere, answeringHere, commandsHere, _expanded,
                (rows, pending, answering, commands, visible) => visible ? null
                    : SessionStatusDots.Rollup(rows.Items, pending, answering, commands))
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
            .TransformWithInlineUpdate(row => new RailSessionViewModel(row, selectedAgentId, agentsWithPending, remoteStale,
                openLocal, openRemote, time, agentsAwaitingAnswer, agentsRunningCommands,
                pullRequests, localOnAppServer, openPullRequest, ageTicks), (vm, row) => vm.Update(row))
            .DisposeMany()
            .SortAndBind(_sessionsSource, SessionComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    internal void Expand() => SetExpanded(true);

    void SetExpanded(bool expanded) {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        _setExpanded(expanded);
        _expanded.OnNext(expanded);
        this.RaisePropertyChanged(nameof(ToggleAccessibleName));
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
