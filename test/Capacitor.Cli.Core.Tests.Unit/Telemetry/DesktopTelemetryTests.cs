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
