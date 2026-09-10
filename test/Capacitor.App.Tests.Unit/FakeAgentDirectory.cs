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

    IObservableCache<AgentRow, string> IAgentDirectory.Rows => Rows;
    IObservable<bool> IAgentDirectory.RemoteStale => RemoteStale;

    public IObservable<IReadOnlyDictionary<string, string>> SessionAgents =>
        Rows.Connect().QueryWhenChanged(q => (IReadOnlyDictionary<string, string>)q.Items
                .Where(r => r.SessionId is { Length: > 0 })
                .ToDictionary(r => r.SessionId!, r => r.Id, StringComparer.Ordinal))
            .StartWith((IReadOnlyDictionary<string, string>)FrozenDictionary<string, string>.Empty);

    public string? VendorOfSession(string sessionId) =>
        Rows.Items.FirstOrDefault(r => r.SessionId == sessionId)?.Vendor;

    public void Dispose() {
        Rows.Dispose();
        RemoteStale.Dispose();
    }
}
