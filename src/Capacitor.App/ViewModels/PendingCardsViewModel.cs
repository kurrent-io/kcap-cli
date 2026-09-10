using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The NEEDS YOU cards pipeline for one agent, off the shared permission cache: filtered by
/// agent, marshalled to the UI thread, and rendered as one of the three card kinds. Shared by
/// ChatTabViewModel (which folds Requests into its own tool-row marks) and hosted on its own for
/// a session with no chat pane.
public sealed class PendingCardsViewModel : ReactiveObject, IDisposable {
    readonly CompositeDisposable _disposables = new();

    public ReadOnlyObservableCollection<PendingCardViewModel> PendingCards { get; }

    /// The filtered, UI-marshalled change stream a chat tab folds into its own request map.
    public IObservable<IChangeSet<PendingPermissionRequest, string>> Requests { get; }

    readonly ObservableAsPropertyHelper<bool> _hasPendingCards;
    public bool HasPendingCards => _hasPendingCards.Value;

    public PendingCardsViewModel(string agentId, IPermissionService permissions, IObservable<string?> root) {
        // ObserveOn BEFORE the binding operator: the cache is mutated on the service's
        // background continuations (IPermissionService.Pending's own doc comment).
        Requests = permissions.Pending
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(p => p.AgentId == agentId);

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
