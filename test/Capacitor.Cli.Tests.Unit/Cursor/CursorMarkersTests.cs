using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// AI-1382 D0/D1 — round-trips CursorMarkers' quarantine/barrier/heartbeat path helpers and the
/// quarantine read/write cycle. Shares the KCAP_CONFIG_DIR temp dir RepoPathStoreGlobalSetup pins
/// before PathHelpers' static ConfigDir field is first touched (see that class's doc comment); a
/// fresh GUID session id per test keeps these from colliding with each other or with the other
/// path-based test classes sharing that same directory.
/// </summary>
public class CursorMarkersTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task Paths_are_dot_namespaced_under_the_shared_config_dir() {
        var sid = NewSessionId();

        await Assert.That(CursorMarkers.QuarantinePath(sid))
            .IsEqualTo(Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "cursor-quarantine", $"{sid}.json"));
        await Assert.That(CursorMarkers.BarrierPath(sid))
            .IsEqualTo(Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "cursor-barrier", $"{sid}.json"));
        await Assert.That(CursorMarkers.HeartbeatPath(sid))
            .IsEqualTo(Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "cursor-heartbeat", $"{sid}.json"));
    }

    [Test]
    public async Task IsQuarantined_false_before_any_marker_is_written() {
        var sid = NewSessionId();

        await Assert.That(CursorMarkers.IsQuarantined(sid)).IsFalse();
    }

    [Test]
    public async Task Quarantine_writes_a_marker_IsQuarantined_reads_it_back() {
        var sid = NewSessionId();

        CursorMarkers.Quarantine(sid, "rewrite detected");

        await Assert.That(CursorMarkers.IsQuarantined(sid)).IsTrue();

        var marker = CursorMarkers.ReadMarker(sid);

        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Value.Reason).IsEqualTo("rewrite detected");
    }

    [Test]
    public async Task Quarantine_keeps_the_first_reason_on_a_second_call() {
        var sid = NewSessionId();

        CursorMarkers.Quarantine(sid, "first reason");
        CursorMarkers.Quarantine(sid, "second reason");

        var marker = CursorMarkers.ReadMarker(sid);

        await Assert.That(marker!.Value.Reason).IsEqualTo("first reason");
    }

    [Test]
    public async Task ReadMarker_null_when_no_marker_written() {
        var sid = NewSessionId();

        await Assert.That(CursorMarkers.ReadMarker(sid)).IsNull();
    }

    [Test]
    public async Task BarrierPending_false_when_no_barrier_created() {
        var sid = NewSessionId();

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task Barrier_pending_until_cleared_then_expires_past_bound() {
        var sid = NewSessionId();
        var now = DateTimeOffset.UtcNow;

        CursorMarkers.CreateBarrier(sid, now);

        await Assert.That(CursorMarkers.BarrierPending(sid, now.AddSeconds(5), TimeSpan.FromSeconds(60))).IsTrue();
        await Assert.That(CursorMarkers.BarrierPending(sid, now.AddSeconds(61), TimeSpan.FromSeconds(60))).IsFalse(); // expired — proceeds

        CursorMarkers.ClearBarrier(sid);

        await Assert.That(CursorMarkers.BarrierPending(sid, now.AddSeconds(5), TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task ClearBarrier_is_a_noop_when_nothing_was_created() {
        var sid = NewSessionId();

        CursorMarkers.ClearBarrier(sid); // must not throw

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task TouchHeartbeat_writes_a_timestamp_WatcherHeartbeat_can_read_back() {
        var sid = NewSessionId();
        var now = DateTimeOffset.UtcNow;

        CursorMarkers.TouchHeartbeat(sid, now);

        await Assert.That(WatcherHeartbeat.Read(CursorMarkers.HeartbeatPath(sid))).IsEqualTo(now);
    }

    // AI-1382 review fix #5 — the durable per-child subagent-start-acknowledgement marker.
    [Test]
    public async Task HasSubagentStartAck_false_before_any_ack_is_recorded() {
        var childSid = NewSessionId();

        await Assert.That(CursorMarkers.HasSubagentStartAck(childSid)).IsFalse();
    }

    [Test]
    public async Task MarkSubagentStartAcked_makes_HasSubagentStartAck_true() {
        var childSid = NewSessionId();

        CursorMarkers.MarkSubagentStartAcked(childSid);

        await Assert.That(CursorMarkers.HasSubagentStartAck(childSid)).IsTrue();
    }

    [Test]
    public async Task SubagentStartAckPath_is_dot_namespaced_under_the_shared_config_dir_and_keyed_by_child() {
        var childSid = NewSessionId();

        await Assert.That(CursorMarkers.SubagentStartAckPath(childSid))
            .IsEqualTo(Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "cursor-subagent-start-ack", $"{childSid}.json"));
    }
}
