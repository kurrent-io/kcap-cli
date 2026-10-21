using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

// A root per test covers both files: SetEnabled(false) deletes the device id as a side effect (see
// TelemetryState.SetEnabled), and one root puts it where this test alone can see it. See
// TelemetryDeviceIdTests for the device-id-specific tests.
public class TelemetryStateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        var state = TelemetryState.Read(Config.Root);

        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Set_enabled_persists_and_survives_reread() {
        TelemetryState.SetEnabled(false, Config.Root);
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();

        TelemetryState.SetEnabled(true, Config.Root);
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsTrue();
    }

    [Test]
    public async Task Set_enabled_reports_a_choice_it_could_not_save() {
        await Assert.That(TelemetryState.SetEnabled(true, Config.Root)).IsTrue();

        File.Delete(Config.Root.Path("telemetry.json"));
        Directory.CreateDirectory(Config.Root.Path("telemetry.json"));

        await Assert.That(TelemetryState.SetEnabled(false, Config.Root)).IsFalse();
    }

    /// Only the call that sets the marker says so, which is what keeps racing processes to one first-run event.
    [Test]
    public async Task Notice_shown_marker_persists_and_only_its_first_setting_reports_true() {
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsFalse();
        await Assert.That(TelemetryState.MarkNoticeShown(Config.Root)).IsTrue();
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsTrue();
        await Assert.That(TelemetryState.MarkNoticeShown(Config.Root)).IsFalse();
    }

    /// A choice that cannot be read may be an opt-out, so it reads as one wherever the saved choice is asked for.
    [Test]
    public async Task An_unreadable_file_reads_as_opted_out() {
        await File.WriteAllTextAsync(Config.Root.Path("telemetry.json"), "{not json");

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root) == false).IsTrue();
    }

    /// Writing the marker over a file it cannot read would erase an opt-out held there.
    [Test]
    public async Task The_notice_marker_never_writes_over_an_unreadable_file() {
        await File.WriteAllTextAsync(Config.Root.Path("telemetry.json"), "{not json");

        await Assert.That(TelemetryState.MarkNoticeShown(Config.Root)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Config.Root.Path("telemetry.json"))).IsEqualTo("{not json");
    }

    /// A choice the process cannot read is not taken for no choice, which would read as enabled.
    [Test]
    public async Task A_state_file_that_cannot_be_read_is_not_taken_for_no_choice() {
        Directory.CreateDirectory(Config.Root.Path("telemetry.json"));

        await Assert.That(TelemetryState.TryRead(Config.Root)).IsNull();
    }

    /// Spooled events from before an opt-out never go out after a later opt-in.
    [Test]
    public async Task Opting_out_clears_the_shared_spool() {
        TelemetrySpool.For(Config.Root).Append([new TelemetryEvent("cli_command", new(), DateTimeOffset.UnixEpoch)]);

        TelemetryState.SetEnabled(false, Config.Root);

        await Assert.That(File.Exists(Config.Root.Path("telemetry-spool.jsonl"))).IsFalse();
    }

    [Test]
    public async Task Mark_notice_shown_preserves_enabled() {
        TelemetryState.SetEnabled(true, Config.Root);

        TelemetryState.MarkNoticeShown(Config.Root);

        var state = TelemetryState.Read(Config.Root);
        await Assert.That(state.Enabled).IsTrue();
        await Assert.That(state.NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        File.WriteAllText(Config.PathTo("telemetry.json"), "{ not json");

        var state = TelemetryState.Read(Config.Root);

        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    // Guards the litter risk the temp-file-then-rename write introduced: a failed or half-finished
    // write must not leave a stray file next to telemetry.json.
    //
    // It deliberately does NOT prove atomicity, and would have passed against the old
    // File.WriteAllText too — interleaving a read with a write to observe a torn file isn't
    // something a unit test can do deterministically across platforms. Atomicity rests on
    // File.Move(overwrite: true) being a same-volume rename; this test only covers the cleanup
    // half of that change.
    [Test]
    public async Task Write_leaves_no_temp_file_behind_after_a_successful_mutation() {
        var path = Config.PathTo("telemetry.json");

        TelemetryState.SetEnabled(false, Config.Root);

        var dir     = Path.GetDirectoryName(path)!;
        var entries = Directory.GetFiles(dir);
        await Assert.That(entries).IsEquivalentTo(new[] { path });
    }
}
