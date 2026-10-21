using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>The desktop app reports under its own source, shows its disclosure on screen rather than on a
/// stderr nobody reads, and never blocks the UI thread on a flush.</summary>
public class DesktopTelemetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static TelemetryStartup Desktop => TelemetryStartup.ForDesktopApp(serverUrl: null, AuthEndpoints.DefaultSignupUrl);

    (CliTelemetry Telemetry, T Sink) Start<T>(T sink) where T : ITelemetrySink =>
        (CliTelemetry.Start(Desktop, Config.Root, TimeProvider.System, () => sink), sink);

    [Test]
    public async Task Events_are_labelled_with_the_desktop_source() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        telemetry.Funnel.EditionOffered();

        await Assert.That(sink.Events[^1].Properties["source"]!.GetValue<string>()).IsEqualTo("desktop");
    }

    [Test]
    public async Task An_eager_capture_returns_while_its_flush_is_still_in_flight() {
        var (telemetry, sink) = Start(new HeldSink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        await Task.Run(() => telemetry.CaptureNow("probe", new JsonObject())).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(await sink.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        sink.Release.SetResult(true);
    }

    [Test]
    public async Task The_notice_is_due_once_and_acknowledging_it_records_the_first_run() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        await Assert.That(telemetry.NoticeDue(Config.Root)).IsTrue();
        telemetry.AcknowledgeNotice(Config.Root);
        telemetry.AcknowledgeNotice(Config.Root);

        await Assert.That(telemetry.NoticeDue(Config.Root)).IsFalse();
        await Assert.That(sink.Names.Count(n => n == "cli_first_run")).IsEqualTo(1);
    }

    /// <summary>The app can run several sign-ins in one process, and the join's loopback hop serves one
    /// return only, so the app mints no join key.</summary>
    [Test]
    public async Task The_desktop_facade_mints_no_join_key() {
        var (telemetry, _) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        await Assert.That(telemetry.Join.Current).IsNull();
    }

    /// <summary>The marker is saved before the event is queued, so the event ships straight away rather
    /// than waiting for a flush a killed app would never reach.</summary>
    [Test]
    public async Task Acknowledging_the_notice_ships_the_first_run_event_at_once() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        telemetry.AcknowledgeNotice(Config.Root);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.Flushes == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Assert.That(sink.Flushes).IsGreaterThan(0);
    }

    /// <summary>The app outlives the commands run beside it, so `kcap config set telemetry off` stops it
    /// sending what it queued before the opt-out as well as anything after.</summary>
    [Test]
    public async Task An_opt_out_saved_while_the_app_runs_stops_it_sending() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);
        telemetry.Capture("before", new JsonObject());

        TelemetryState.SetEnabled(false, Config.Root);
        telemetry.Capture("after", new JsonObject());
        await telemetry.FlushAndClose();

        await Assert.That(sink.Events).IsEmpty();
        await Assert.That(sink.Flushes).IsEqualTo(0);
        await Assert.That(telemetry.Enabled).IsFalse();
    }

    /// <summary>A saved file it cannot read may hold an opt-out, so nothing is sent on it; but a read that fails
    /// for a moment is not an opt-out, so sending resumes once the choice reads again.</summary>
    [Test]
    public async Task An_unreadable_saved_choice_sends_nothing_until_it_reads_again() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        await File.WriteAllTextAsync(Config.Root.Path("telemetry.json"), "{not json");
        telemetry.Capture("unreadable", new JsonObject());
        await Assert.That(sink.Events).IsEmpty();
        await Assert.That(telemetry.Enabled).IsTrue();

        TelemetryState.SetEnabled(true, Config.Root);
        telemetry.Capture("readable", new JsonObject());
        await Assert.That(sink.Events.Select(e => e.Name).ToList()).IsEquivalentTo(new List<string> { "readable" });
    }

    /// <summary>An opt-out and an opt-in made elsewhere between two captures replace the device id; sending on
    /// under the old one would link the two sides of the opt-out, so the app stops instead.</summary>
    [Test]
    public async Task A_device_id_replaced_while_the_app_runs_stops_it_sending() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        TelemetryState.SetEnabled(false, Config.Root);
        TelemetryState.SetEnabled(true, Config.Root);
        TelemetryDeviceId.GetOrCreate(Config.Root);
        telemetry.Capture("after", new JsonObject());

        await Assert.That(sink.Events).IsEmpty();
        await Assert.That(telemetry.Enabled).IsFalse();
        // The spool now holds what the CLI queued under the new id; this stop is this process's own.
        await Assert.That(sink.Abandoned).IsTrue();
        await Assert.That(sink.Discarded).IsFalse();
    }

    /// <summary>A device id that cannot be read just now is not a replaced one: nothing is sent on it, but the app
    /// keeps sending once it reads again.</summary>
    [Test]
    public async Task An_unreadable_device_id_sends_nothing_until_it_reads_again() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);
        var path = Config.Root.Path("telemetry-device.json");
        var saved = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(path, "{not json");
        telemetry.Capture("unreadable", new JsonObject());
        await Assert.That(telemetry.Enabled).IsTrue();

        await File.WriteAllTextAsync(path, saved);
        telemetry.Capture("readable", new JsonObject());
        await Assert.That(sink.Events.Select(e => e.Name).ToList()).IsEquivalentTo(new List<string> { "readable" });
    }

    /// <summary>The once-per-device event goes only with the marker that says it went. A file that cannot be read
    /// may hold an opt-out, so it shows no notice either, rather than one on every launch.</summary>
    [Test]
    public async Task An_acknowledgement_that_cannot_be_saved_sends_no_first_run() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);
        Directory.CreateDirectory(Config.Root.Path("telemetry.json"));

        telemetry.AcknowledgeNotice(Config.Root);

        await Assert.That(sink.Names).DoesNotContain("cli_first_run");
        await Assert.That(telemetry.NoticeDue(Config.Root)).IsFalse();
    }

    [Test]
    public async Task The_console_announcement_never_runs_for_the_desktop_app() {
        var (telemetry, sink) = Start(new RecordingTelemetrySink());
        TelemetryTestGuards.AssertEnabled("app", Config.Root, telemetry);

        telemetry.Announce(Config.Root);

        await Assert.That(sink.Names).DoesNotContain("cli_first_run");
        await Assert.That(telemetry.NoticeDue(Config.Root)).IsTrue();
    }

    sealed class HeldSink : ITelemetrySink {
        public readonly TaskCompletionSource<bool> FlushStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release      = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enqueue(TelemetryEvent e) { }
        public void Discard() { }

        public Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
            FlushStarted.TrySetResult(true);

            return Release.Task;
        }
    }
}
