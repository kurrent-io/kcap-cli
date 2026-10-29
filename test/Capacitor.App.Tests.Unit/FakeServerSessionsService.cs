using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Scripted IServerSessionsService: caches and subjects a test edits directly, and recorded calls.
sealed class FakeServerSessionsService : IServerSessionsService, IDisposable {
    public readonly SourceCache<RepoSessionDto, string> LiveCache = new(r => r.SessionId);
    public readonly SourceCache<RepoSessionDto, string> MineLiveCache = new(r => r.SessionId);
    public readonly SourceCache<RepoSessionDto, string> EndedCache = new(r => r.SessionId);
    public readonly BehaviorSubject<ServerSessionsPaging> PagingSubject = new(new(0, 0, 1, false, false));
    public readonly BehaviorSubject<int> LiveNotShownSubject = new(0);
    public readonly BehaviorSubject<IReadOnlySet<string>> ViewerIdsSubject = new(FrozenSet<string>.Empty);
    public readonly BehaviorSubject<string?> ViewerUserIdSubject = new(null);
    public readonly BehaviorSubject<bool> CurrentSubject = new(true);
    readonly BehaviorSubject<RailScope> _scope = new(RailScope.Mine);
    public int ShowMoreCalls;
    public int ReconcileCalls;

    public IObservableCache<RepoSessionDto, string> Live => LiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> MineLive => MineLiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> Ended => EndedCache.AsObservableCache();
    public string? Kept;
    public readonly Subject<string> DroppedSubject = new();
    public IObservable<string> Dropped => DroppedSubject;
    /// Derived from the three caches, so a test edits one cache and the directory sees the whole state.
    public IObservable<ServerSessionsSnapshot> Snapshots => Observable.CombineLatest(
        LiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        MineLiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        EndedCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        (live, mine, ended) => new ServerSessionsSnapshot([.. live], [.. mine], [.. ended]));
    public void Keep(string? sessionId) => Kept = sessionId;
    public readonly List<(string SessionId, string Title)> Renames = [];
    public void ApplyRename(string sessionId, string title) => Renames.Add((sessionId, title));
    public IObservable<ServerSessionsPaging> Paging => PagingSubject;
    public IObservable<int> LiveNotShown => LiveNotShownSubject;
    public IObservable<IReadOnlySet<string>> ViewerIds => ViewerIdsSubject;
    public IObservable<string?> ViewerUserId => ViewerUserIdSubject;
    public IObservable<bool> Current => CurrentSubject;
    public RailScope Scope { get => _scope.Value; set { if (_scope.Value != value) _scope.OnNext(value); } }
    public IObservable<RailScope> ScopeChanges => _scope;

    public Task ShowMoreAsync() { ShowMoreCalls++; return Task.CompletedTask; }
    public Task ReconcileAsync() { ReconcileCalls++; return Task.CompletedTask; }

    public void Dispose() {
        LiveCache.Dispose();
        MineLiveCache.Dispose();
        EndedCache.Dispose();
    }
}
