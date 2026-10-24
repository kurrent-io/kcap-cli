using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Scripted IRemoteAgentsService: a SourceCache the test edits directly plus a BehaviorSubject
/// for Daemons, for any suite that needs remote rows in an AgentDirectory.
sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
    public readonly SourceCache<AgentInstanceDto, string> Cache = new(a => a.AgentId);
    public readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject = new([]);
    /// True unless a test is about retained rows: the cache a test edits is what the registry says now.
    public readonly BehaviorSubject<bool> CurrentSubject = new(true);
    public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
    public IObservable<bool> Current => CurrentSubject;
    public void Dispose() => Cache.Dispose();
}
