using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Scripted IRemoteAgentsService: a SourceCache the test edits directly plus a BehaviorSubject
/// per stream, for any suite that needs remote rows in an AgentDirectory.
sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
    public readonly SourceCache<AgentInstanceDto, string> Cache = new(a => a.AgentId);
    public readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject = new([]);
    public readonly BehaviorSubject<DaemonListing> ListingSubject = new(new(null, 0, false));
    public readonly BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> DaemonAccountsSubject = new([]);
    public readonly BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccountsSubject = new([]);
    /// True unless a test is about retained rows: the cache a test edits is what the registry says now.
    public readonly BehaviorSubject<bool> CurrentSubject = new(true);
    public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
    public IObservable<DaemonListing> Listing => ListingSubject;
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts => DaemonAccountsSubject;
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccounts => DrawableDaemonAccountsSubject;
    public IObservable<bool> Current => CurrentSubject;
    public Task RefreshDaemonsAsync() => Task.CompletedTask;
    public Func<string, string, string, Task<SessionRenameResult>> Rename { get; set; } =
        (_, _, _) => Task.FromResult(new SessionRenameResult(false, null, "unauthenticated"));
    public Task<SessionRenameResult> RenameAsync(string agentId, string sessionId, string title, CancellationToken ct) =>
        Rename(agentId, sessionId, title);
    public void Dispose() => Cache.Dispose();
}
