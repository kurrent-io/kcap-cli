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
}
