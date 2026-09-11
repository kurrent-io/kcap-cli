using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// A directory a test edits row by row, skipping AgentDirectory's two-lane merge. Rows and
/// RemoteStale are the writable types so a test can drive them; the interface members hand the
/// same instances to the code under test.
sealed class FakeAgentDirectory : IAgentDirectory, IDisposable {
    public SourceCache<AgentRow, string> Rows { get; } = new(r => r.Key);
    public BehaviorSubject<bool> RemoteStale { get; } = new(false);

    /// True unless a test is about server scoping: the local daemon shares the app's server.
    public BehaviorSubject<bool> LocalOnAppServer { get; } = new(true);
    /// The agent ids the directory reports the local daemon has proven it hosts.
    public HashSet<string> ProvenTwins { get; } = new(StringComparer.Ordinal);

    IObservableCache<AgentRow, string> IAgentDirectory.Rows => Rows;
    IObservable<bool> IAgentDirectory.RemoteStale => RemoteStale;
    IObservable<bool> IAgentDirectory.LocalDaemonOnAppServer => LocalOnAppServer;

    public bool IsProvenLocalTwin(string agentId) => ProvenTwins.Contains(agentId);

    public IObservable<IReadOnlyDictionary<string, string>> SessionAgents =>
        Rows.Connect().QueryWhenChanged(q => SessionMap(q.Items))
            .StartWith((IReadOnlyDictionary<string, string>)FrozenDictionary<string, string>.Empty);

    public string? VendorOfSession(string sessionId) =>
        Rows.Items.Where(r => r.SessionId == sessionId).OrderBy(r => r.Origin).Select(r => r.Vendor).FirstOrDefault();

    /// AgentDirectory's own rule: Local sorts before Remote, so a session an unproven twin pair
    /// claims on both lanes resolves to the local row rather than throwing on the duplicate.
    static IReadOnlyDictionary<string, string> SessionMap(IEnumerable<AgentRow> rows) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(r => r.Origin))
            if (row.SessionId is { Length: > 0 } sid) map.TryAdd(sid, row.Id);
        return map;
    }

    public void Dispose() {
        Rows.Dispose();
        RemoteStale.Dispose();
        LocalOnAppServer.Dispose();
    }
}
