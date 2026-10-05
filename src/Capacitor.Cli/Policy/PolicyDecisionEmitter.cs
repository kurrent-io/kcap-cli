namespace Capacitor.Cli.Policy;

using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// Appends policy events to the hook spool, where the throttled drain that carries every other
/// lifecycle event picks them up. Nothing is posted inline: a decision seam runs under a 5s hook
/// ceiling and the vendor acts on the seam's stdout only once the process exits, so a round trip
/// here could outlive the hook and lose a deny that had already been written.
/// </summary>
internal sealed class PolicyDecisionEmitter(ConfigRoot config, TimeProvider time) {
    /// <param name="snapshot">Null only when the decision names no resolvable snapshot — a failure
    /// that never got one — so there is nothing to upload alongside it.</param>
    public Task EmitAsync(PolicyDecisionEventV1 evt, PolicySnapshot? snapshot) {
        try {
            var spool = new HookSpool(config, time);
            // Snapshot first: a decision names a snapshot id the server cannot resolve on its own,
            // and the spool delivers a session's entries in arrival order.
            if (snapshot is not null) EnsureSnapshotSpooled(spool, evt.SessionId, snapshot);
            var body = JsonSerializer.Serialize(evt, CapacitorJsonContext.Default.PolicyDecisionEventV1);
            spool.Append(evt.SessionId, "policy-decision", body);
        }
        catch { }

        return Task.CompletedTask;
    }

    // Sanitized like the snapshot and journal keys: a raw id carrying a path separator would put the
    // marker in a nested directory the session-end eviction never looks in, and that eviction
    // matches the `{key}-` prefix both markers share.
    static string SpooledMarker(ConfigRoot config, string sessionId, string snapshotId) =>
        config.Path("policy", "uploaded",
            $"{PolicySnapshotStore.Sanitize(sessionId)}-{snapshotId[..Math.Min(16, snapshotId.Length)]}");

    static string DeliveredMarker(ConfigRoot config, string sessionId, string snapshotId) =>
        SpooledMarker(config, sessionId, snapshotId) + ".delivered";

    /// <summary>Whether the server has acknowledged this session's snapshot upload. Until it has, a
    /// judge request carries the snapshot inline; a missed mark only costs the bytes again.</summary>
    internal static bool IsSnapshotDelivered(ConfigRoot config, string sessionId, string snapshotId) =>
        File.Exists(DeliveredMarker(config, sessionId, snapshotId));

    /// <summary>Called by a drain poster once the server accepted a <c>policy-snapshot</c> entry.</summary>
    internal static void MarkSnapshotDelivered(ConfigRoot config, string uploadBody) {
        try {
            var upload = JsonSerializer.Deserialize(uploadBody, CapacitorJsonContext.Default.PolicySnapshotUploadV1);
            if (upload is not { SessionId: { Length: > 0 } sid, SnapshotId: { Length: > 0 } snap }) return;
            var marker = DeliveredMarker(config, sid, snap);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        } catch { }
    }

    void EnsureSnapshotSpooled(HookSpool spool, string sessionId, PolicySnapshot snapshot) {
        var marker = SpooledMarker(config, sessionId, snapshot.Id);
        if (File.Exists(marker)) return;
        var body = JsonSerializer.Serialize(PolicyWire.ToUpload(sessionId, snapshot),
            CapacitorJsonContext.Default.PolicySnapshotUploadV1);
        // The marker may only be written once the append actually persisted, or a failed write would
        // suppress every later attempt and leave the decisions unresolvable.
        if (!spool.Append(sessionId, "policy-snapshot", body)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "");
    }
}
