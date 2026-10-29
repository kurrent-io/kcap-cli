namespace Capacitor.Cli.Core.LocalIpc;

/// <param name="ReadOnlyReason">Why the daemon refuses input; null for a read-write attach.</param>
/// <param name="RuntimeStartedAt">The attached runtime's start, as the resume ledger stamps it; null
/// from a daemon that does not report it.</param>
public sealed record AttachAck(byte[] Snapshot, string? ReadOnlyReason, DateTimeOffset? RuntimeStartedAt);
