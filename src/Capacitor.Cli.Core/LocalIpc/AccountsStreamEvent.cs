using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.LocalIpc;

/// One accounts subscription attempt as a typed stream. `Subscribed` is a client-local boundary
/// emitted after the subscribe write flushes; it does not prove the daemon registered the
/// subscription, and the daemon writes nothing until its first snapshot exists.
public abstract record AccountsStreamEvent {
    public sealed record Subscribed : AccountsStreamEvent;
    public sealed record Snapshot(AccountsSnapshotDto Value) : AccountsStreamEvent;
}
