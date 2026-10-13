using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The session rail's root: repository → sessions over directory.Rows.
/// Ctor-scoped and disposable like HomeViewModel — the tree must be live from construction.
/// ONE ObserveOn at the top of the pipeline: nested group caches are mutated by this outer
/// pipeline, so every inner Connect() below already fires on the UI thread.
public sealed class SessionRailViewModel : ReactiveObject, IDisposable {
    readonly CompositeDisposable _disposables = new();

    // SelectedAgentId is fed to the nested VMs via this subject, not this.WhenAnyValue: that
    // call routes through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which is
    // only reliably primed when some other test has already pumped the headless dispatcher
    // first — AvaloniaSession.cs documents the MainThreadScheduler initialization race.
    readonly BehaviorSubject<string?> _selectedAgentIdChanges = new(null);

    string? _selectedAgentId;
    public string? SelectedAgentId {
        get => _selectedAgentId;
        set {
            this.RaiseAndSetIfChanged(ref _selectedAgentId, value);
            _selectedAgentIdChanges.OnNext(value);
        }
    }

    readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;

    readonly ObservableCollectionExtended<RailRepoViewModel> _reposSource = new();
    public ReadOnlyObservableCollection<RailRepoViewModel> Repos { get; }

    // No-repository last, then leaf label; root path tiebreak.
    static readonly IComparer<RailRepoViewModel> RepoComparer =
        Comparer<RailRepoViewModel>.Create((a, b) => {
            var byNoRepo = a.IsNoRepository.CompareTo(b.IsNoRepository);
            if (byNoRepo != 0) return byNoRepo;
            var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(a.RootPath, b.RootPath);
        });

    public SessionRailViewModel(
            IAgentDirectory directory,
            Action<string> openLocalSession, Action<string> openRemoteSession, TimeProvider time,
            IObservable<IReadOnlySet<string>>? agentsWithPending = null,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null,
            IObservable<IReadOnlyDictionary<string, int>>? agentsRunningCommands = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequests = null,
            Action<string, AgentOrigin>? openPullRequest = null) {
        IObservable<string?> selected = _selectedAgentIdChanges;
        // PermissionService.AgentsWithPending emits from background continuations; marshal once
        // here so every nested OAPH downstream sees
        // it on the UI thread without adding its own ObserveOn.
        var pending = (agentsWithPending ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var answering = (agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        // Marshaled once here, like `pending` above, so every nested OAPH downstream sees it on
        // the UI thread without its own ObserveOn.
        var stale = directory.RemoteStale.ObserveOn(RxSchedulers.MainThreadScheduler);
        var prs = (pullRequests ?? Observable.Return<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                FrozenDictionary<string, RailPullRequestSummary>.Empty)).ObserveOn(RxSchedulers.MainThreadScheduler);
        var localOnAppServer = directory.LocalDaemonOnAppServer.ObserveOn(RxSchedulers.MainThreadScheduler);
        var commands = (agentsRunningCommands ?? Observable.Return<IReadOnlyDictionary<string, int>>(
                FrozenDictionary<string, int>.Empty)).ObserveOn(RxSchedulers.MainThreadScheduler);

        _isEmpty = directory.Rows.CountChanged
            .Select(c => c == 0)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.IsEmpty, initialValue: directory.Rows.Count == 0)
            .DisposeWith(_disposables);

        Repos = new ReadOnlyObservableCollection<RailRepoViewModel>(_reposSource);
        directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Group(r => r.RepoGroupKey)
            .Transform(g => new RailRepoViewModel(
                g, selected, pending, stale, openLocalSession, openRemoteSession, time, answering, commands,
                prs, localOnAppServer, openPullRequest))
            .DisposeMany()
            .SortAndBind(_reposSource, RepoComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    internal static string WorktreeKeyFor(AgentStatusDto dto) =>
        PlatformPaths.Normalize(CheckoutLabel.CheckoutPathFor(dto) ?? dto.RepoPath ?? "");

    internal static string WorktreeKeyFor(AgentRow row) => row.CheckoutKey;

    public void Dispose() => _disposables.Dispose();
}
