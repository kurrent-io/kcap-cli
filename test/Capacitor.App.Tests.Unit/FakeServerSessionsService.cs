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
    public readonly BehaviorSubject<RepoSessionDto?> PinnedSubject = new(null);
    public readonly BehaviorSubject<string?> PinnedIdSubject = new(null);
    public readonly BehaviorSubject<SessionIdentity> IdentitySubject = new(new(0, 1, true));
    /// What PinAsync answers; a Pinned answer also sets the pinned id.
    public PinOutcome PinResult = PinOutcome.Pinned;
    public readonly List<(string SessionId, PinPresentation Presentation)> PinCalls = [];
    public int UnpinCalls;
    public int ShowMoreCalls;
    public int ReconcileCalls;

    public IObservableCache<RepoSessionDto, string> Live => LiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> MineLive => MineLiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> Ended => EndedCache.AsObservableCache();
    public string? Kept;
    public readonly Subject<string> DroppedSubject = new();
    public IObservable<string> Dropped => DroppedSubject;
    /// Derived from the three caches and the pin subjects, so a test edits one and the directory sees the whole state.
    public IObservable<ServerSessionsSnapshot> Snapshots => Observable.CombineLatest(
        LiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        MineLiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        EndedCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        PinnedSubject, PinnedIdSubject,
        (live, mine, ended, pinned, pinnedId) => new ServerSessionsSnapshot([.. live], [.. mine], [.. ended], pinned, pinnedId));
    public IObservable<SessionIdentity> Identity => IdentitySubject;
    public string? PinnedSessionId => PinnedIdSubject.Value;

    /// When set, PinAsync answers once the test completes it rather than with PinResult.
    public TaskCompletionSource<PinOutcome>? PinGate;

    public async Task<PinOutcome> PinAsync(string sessionId, PinPresentation presentation, CancellationToken ct) {
        PinCalls.Add((sessionId, presentation));
        var result = PinGate is { } gate ? await gate.Task : PinResult;
        if (result == PinOutcome.Pinned) PinnedIdSubject.OnNext(sessionId);
        return result;
    }

    public void Unpin() {
        UnpinCalls++;
        PinnedSubject.OnNext(null);
        PinnedIdSubject.OnNext(null);
    }
    public void Keep(string? sessionId) => Kept = sessionId;
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
