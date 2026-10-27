using System.Text.Json;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class StaleTxnMarkerTests {
    const string Plist = "<plist>unit</plist>";
    static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    static TxnMarker Marker(string? fingerprint, string operation = "replace", string preState = "absent|nounit||pid=") =>
        new(1, operation, "bootstrapped", preState, "no-unit", fingerprint);

    static ServiceQuery Running(int? jobPid) => new(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", jobPid);

    [Test]
    public async Task Running_job_owned_by_the_daemon_with_the_recorded_unit_took_effect() {
        var took = StaleTxnMarker.TookEffect(
            Marker(ServiceTxnMarker.Fingerprint(Plist)), Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsTrue();
    }

    [Test]
    public async Task A_start_took_effect_once_the_service_runs() {
        var took = StaleTxnMarker.TookEffect(Marker(null, "start"), Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsTrue();
    }

    [Test]
    public async Task A_start_that_replaced_the_recorded_job_took_effect() {
        var marker = Marker(null, "start", "loaded|unit|/u/kcap-daemon|pid=41");
        var took = StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsTrue();
    }

    [Test]
    public async Task A_start_interrupted_with_the_recorded_job_still_running_stays_surfaced() {
        var marker = Marker(null, "start", "loaded|unit|/u/kcap-daemon|pid=42") with { Phase = "captured" };
        var took = StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsFalse();
    }

    [Test]
    public async Task A_start_still_captured_stays_surfaced_even_with_a_new_job_running() {
        var marker = Marker(null, "start") with { Phase = "captured" };
        var took = StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsFalse();
    }

    [Test]
    [Arguments("{\"version\":1,\"operation\":\"start\",\"phase\":\"bootstrapped\",\"safe_state\":\"no-unit\"}")]
    [Arguments("{\"version\":1,\"operation\":\"start\",\"phase\":\"bootstrapped\",\"pre_state\":null,\"safe_state\":\"no-unit\"}")]
    public async Task A_start_marker_without_a_pre_state_stays_surfaced(string json) {
        var marker = JsonSerializer.Deserialize<TxnMarker>(json, SnakeCase)!;
        var took = StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsFalse();
    }

    [Test]
    public async Task A_replace_that_stopped_before_writing_its_unit_stays_surfaced() {
        var took = StaleTxnMarker.TookEffect(Marker(null), Running(42), 42, LaunchdUnit.PlistRead.Ok, Plist);
        await Assert.That(took).IsFalse();
    }

    [Test]
    public async Task A_unit_someone_else_rewrote_stays_surfaced() {
        var took = StaleTxnMarker.TookEffect(
            Marker(ServiceTxnMarker.Fingerprint(Plist)), Running(42), 42, LaunchdUnit.PlistRead.Ok, "<plist>other</plist>");
        await Assert.That(took).IsFalse();
    }

    [Test]
    public async Task A_recorded_unit_that_cannot_be_read_stays_surfaced() {
        var marker = Marker(ServiceTxnMarker.Fingerprint(Plist));
        await Assert.That(StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Absent, null)).IsFalse();
        await Assert.That(StaleTxnMarker.TookEffect(marker, Running(42), 42, LaunchdUnit.PlistRead.Unreadable, null)).IsFalse();
    }

    [Test]
    public async Task A_daemon_that_is_not_the_job_stays_surfaced() {
        var took = StaleTxnMarker.TookEffect(Marker(null, "start"), Running(42), 43, LaunchdUnit.PlistRead.Absent, null);
        await Assert.That(took).IsFalse();
    }

    [Test]
    public async Task A_service_that_is_not_running_stays_surfaced() {
        var stopped = new ServiceQuery(LabelProbe.Absent, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var took = StaleTxnMarker.TookEffect(Marker(null, "start"), stopped, null, LaunchdUnit.PlistRead.Absent, null);
        await Assert.That(took).IsFalse();
    }
}
