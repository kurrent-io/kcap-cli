using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// The server's view of sessions, read from the listing and kept current by its pushes. Every cache
/// is keyed by session id, every observable replays its latest value, and nothing here marshals to
/// the UI thread.
public interface IServerSessionsService {
    /// Every live session the signed-in user may see, whatever the scope; the ownership evidence
    /// for daemon rows, and the live rows under Team.
    IObservableCache<RepoSessionDto, string> Live { get; }
    /// The signed-in user's live sessions, maintained only under Mine; empty under Team.
    IObservableCache<RepoSessionDto, string> MineLive { get; }
    /// The newest ended sessions of the scope, as many pages as were asked for.
    IObservableCache<RepoSessionDto, string> Ended { get; }
    /// All three sets as one read left them, published once per read; replays the latest.
    IObservable<ServerSessionsSnapshot> Snapshots { get; }
    /// Sessions the server deleted, and every listed session when the account changes: the two
    /// removals that are the server's word rather than a page cut or a scope switch.
    IObservable<string> Dropped { get; }
    IObservable<ServerSessionsPaging> Paging { get; }
    /// Live sessions of the scope the listing's offset cap kept out of Live; zero in practice.
    IObservable<int> LiveNotShown { get; }
    /// The signed-in account's ids with every alias the server folds into it; empty until read.
    IObservable<IReadOnlySet<string>> ViewerIds { get; }
    IObservable<string?> ViewerUserId { get; }
    /// True after a successful read in the lane's current connection; false while the lane is down
    /// or after a failed read, when the caches are retained history.
    IObservable<bool> Current { get; }
    RailScope Scope { get; set; }
    IObservable<RailScope> ScopeChanges { get; }
    /// Asks for one more page of ended sessions; refused silently at the offset cap.
    Task ShowMoreAsync();
    /// Re-reads the live listing and the whole ended prefix and replaces the caches with the result.
    Task ReconcileAsync();
    /// The session whose row is open in the rail: it stays in the snapshot's Ended set when a read
    /// no longer lists it, until another (or none) is kept.
    void Keep(string? sessionId);
    /// The account and connection the caches and the pin answer for; replays the latest.
    IObservable<SessionIdentity> Identity { get; }
    string? PinnedSessionId { get; }
    /// Holds a session in the snapshot whether or not a listing supplies it: a listed session pins
    /// at once, any other only once its summary reads back. Nothing is pinned on a failure.
    Task<PinOutcome> PinAsync(string sessionId, PinPresentation presentation, CancellationToken ct);
    void Unpin();
}
