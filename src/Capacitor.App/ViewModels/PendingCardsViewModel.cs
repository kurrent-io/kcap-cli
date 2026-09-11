using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The NEEDS YOU cards pipeline for one workspace, off the shared permission cache: filtered by
/// lane and identity, marshalled to the UI thread, and rendered as one of the three card kinds.
/// Shared by ChatTabViewModel (which folds Requests into its own tool-row marks) and hosted on its
/// own for a session with no chat pane.
public sealed class PendingCardsViewModel : ReactiveObject, IDisposable {
    readonly CompositeDisposable _disposables = new();

    public ReadOnlyObservableCollection<PendingCardViewModel> PendingCards { get; }

    /// The filtered, UI-marshalled change stream a chat tab folds into its own request map.
    public IObservable<IChangeSet<PendingPermissionRequest, string>> Requests { get; }

    readonly ObservableAsPropertyHelper<bool> _hasPendingCards;
    public bool HasPendingCards => _hasPendingCards.Value;

    /// <param name="sessionId">
    /// The owning workspace's session id as it resolves; must replay its current value on
    /// subscribe, since the filter produces nothing until the first one arrives.
    /// </param>
    /// <param name="localDaemonOnAppServer">
    /// IAgentDirectory's verdict, which must replay on subscribe. Null is a caller with no
    /// directory, and admits server items unscoped.
    /// </param>
    public PendingCardsViewModel(
            string agentId, AgentOrigin origin, IObservable<string?> sessionId,
            IPermissionService permissions, IObservable<string?> root,
            IObservable<bool>? localDaemonOnAppServer = null) {
        // A remote workspace's session belongs to the app's own server by construction. A local
        // one's belongs to whatever server the local daemon reports, and a session id is unique
        // only within a server — Claude's derive from transcript filenames and imports preserve
        // them — so a matching id alone would render another server's prompt under this header and
        // answer its process over HTTP.
        var scoped = origin == AgentOrigin.Remote
            ? Observable.Return(true)
            : localDaemonOnAppServer ?? Observable.Return(true);

        // Lane and identity, never the agent id alone: an unproven local/remote pair shares one id
        // while carrying two different agents, so the id alone would render the other lane's card
        // under this header, and answering it would act on a process the user never opened. A
        // server item belongs to whichever workspace holds its session — an ACP question for a
        // local agent included.
        var admits = sessionId.CombineLatest(scoped, (sid, onAppServer) =>
            (Func<PendingPermissionRequest, bool>)(p => p.Lane switch {
                PermissionLane.Local => origin == AgentOrigin.Local && p.AgentId == agentId,
                _ => onAppServer && sid is { Length: > 0 } && p.SessionId == sid,
            }));

        // ObserveOn BEFORE the binding operator: the cache is mutated on the service's
        // background continuations (IPermissionService.Pending's own doc comment).
        Requests = permissions.Pending
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(admits);

        var cards = Requests
            .Transform(p => p switch {
                { Questions: not null } => (PendingCardViewModel)new QuestionCardViewModel(p, permissions),
                { AcpQuestion: not null } => new AcpQuestionCardViewModel(p, permissions),
                _ => new PermissionCardViewModel(p, permissions, root),
            })
            .DisposeMany()
            .SortAndBind(out var pendingCards, Comparer<PendingCardViewModel>.Create((a, b) => {
                var byTime = a.RequestedAt.CompareTo(b.RequestedAt);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.Key, b.Key);
            }));
        PendingCards = pendingCards;

        // Hooked before the pipeline subscribes: on the UI thread the scheduler delivers an
        // already-populated cache inline, so a hook installed afterwards would miss the first fill.
        // The delegate-based overload, not the reflection one: ReadOnlyObservableCollection's
        // CollectionChanged is only reachable through this interface, and the reflection overload
        // (Observable.FromEventPattern(target, eventName)) looks up public events only.
        var notifications = (INotifyCollectionChanged)pendingCards;
        _hasPendingCards = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => notifications.CollectionChanged += h, h => notifications.CollectionChanged -= h)
            .Select(_ => pendingCards.Count > 0)
            .ToProperty(this, x => x.HasPendingCards, initialValue: pendingCards.Count > 0)
            .DisposeWith(_disposables);

        cards.Subscribe().DisposeWith(_disposables);
    }

    public void Dispose() => _disposables.Dispose();
}
