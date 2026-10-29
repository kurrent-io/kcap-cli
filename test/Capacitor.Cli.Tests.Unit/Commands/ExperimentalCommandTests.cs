using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Pins what `kcap experimental` prints for each state the daemon can report, and that it
/// writes the saved switch before it asks.</summary>
[NotInParallel]
public class ExperimentalCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Id = "codex-app-server";

    static DaemonStatusDto Status(List<ExperimentalFeatureStatusDto>? features, string connection = "connected") =>
        new(new DaemonInfoDto("d", "1", "https://x", connection, 5, 0), [], Experimental: features);

    static ExperimentalFeatureStatusDto Feature(bool saved, bool active, bool acked = true, string? reason = null, bool? failed = null) =>
        new(Id, saved, active, 1, acked, reason, failed);

    static ProbeResult Running(DaemonStatusDto status) => new(true, null, status, true);

    static readonly ProbeResult NotRunning = new(false, null, null, false);

    Task<(int Exit, string Out, string Err)> Run(Func<int, ProbeResult> probe, params string[] args) =>
        Run(Resolutions.None(new ConfigRoot(Tmp.Path)), (_, n) => probe(n), args);

    async Task<(int Exit, string Out, string Err)> Run(
            ProfileContext profiles, Func<string, int, ProbeResult> probe, params string[] args) {
        var time  = new FakeTimeProvider();
        var calls = 0;
        var sut = new ExperimentalCommand(
            new ConfigRoot(Tmp.Path), profiles, time,
            (daemon, _) => Task.FromResult(probe(daemon, calls++)),
            (span, _) => { time.Advance(span); return Task.CompletedTask; });

        using var capture = ConsoleOutput.StartFullCapture("\n");
        var exit = await sut.HandleAsync(["experimental", .. args]);

        return (exit, capture.GetCapturedOutput(), capture.GetCapturedError());
    }

    [Test]
    public async Task Enable_prints_applied_live_once_status_reports_the_new_generation() {
        var (exit, output, _) = await Run(
            n => Running(Status([n == 0 ? Feature(false, false) : Feature(true, true)])), "enable", Id);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Applied live");
        await Assert.That(new ExperimentalSettingsStore(new ConfigRoot(Tmp.Path)).Load("default").IsEnabled(Id)).IsTrue();
    }

    [Test]
    public async Task Disable_prints_applied_live_once_the_daemon_reports_it_off() {
        var (_, output, _) = await Run(n => Running(Status([n == 0 ? Feature(true, true) : Feature(false, false)])), "disable", Id);

        await Assert.That(output).Contains("Applied live");
    }

    [Test]
    public async Task Enable_against_an_older_daemon_prints_too_old() {
        var (_, output, _) = await Run(_ => Running(Status(null)), "enable", Id);

        await Assert.That(output).Contains("Saved; the running daemon is too old to apply it. Update kcap and restart the daemon");
    }

    [Test]
    public async Task Enable_times_out_with_the_check_hint() {
        var (exit, output, _) = await Run(_ => Running(Status([Feature(false, false)])), "enable", Id);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Saved; the daemon did not confirm in time. Run kcap experimental to check");
    }

    [Test]
    public async Task Enable_with_no_daemon_prints_applies_on_start() {
        var (_, output, _) = await Run(_ => NotRunning, "enable", Id);

        await Assert.That(output).Contains("Saved; the daemon is not running and will apply it when it starts");
    }

    [Test]
    public async Task Enable_the_daemon_cannot_honour_names_the_reason() {
        var (_, output, _) = await Run(_ => Running(Status([Feature(true, false, reason: "Codex 0.1 is below 0.146.0")])), "enable", Id);

        await Assert.That(output).Contains("Saved, but not active: Codex 0.1 is below 0.146.0");
    }

    /// <summary>The commit is reported before its re-registration completes, so a healthy daemon's first
    /// answer is always unacknowledged.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Enable_waits_for_the_server_to_be_told_before_applied_live(bool firstAttemptFailed) {
        var (_, output, _) = await Run(
            n => Running(Status([n == 0 ? Feature(true, true, acked: false, failed: firstAttemptFailed) : Feature(true, true)])), "enable", Id);

        await Assert.That(output).Contains("Applied live");
        await Assert.That(output).DoesNotContain("Applied locally");
    }

    [Test]
    public async Task Disable_waits_for_the_server_to_be_told_before_applied_live() {
        var (_, output, _) = await Run(n => n switch {
            0 => Running(Status([Feature(false, false, acked: false)])),
            _ => Running(Status([Feature(false, false)])),
        }, "disable", Id);

        await Assert.That(output).Contains("Applied live");
    }

    /// <summary>A probe whose hello and status may have reached different daemons cannot confirm anything,
    /// however settled its snapshot looks.</summary>
    [Test]
    public async Task Enable_keeps_polling_while_the_probe_is_identity_inconsistent() {
        var (_, output, _) = await Run(_ => new ProbeResult(true, null, Status([Feature(true, true)]), false), "enable", Id);

        await Assert.That(output).Contains("Saved; the daemon did not confirm in time. Run kcap experimental to check");
        await Assert.That(output).DoesNotContain("Applied live");
    }

    /// <summary>A registration can fail while the hub stays connected; the daemon's flag, not the
    /// connection, says the server was not told.</summary>
    [Test]
    public async Task Enable_whose_registration_failed_prints_applied_locally_at_the_deadline() {
        var (_, output, _) = await Run(_ => Running(Status([Feature(true, true, acked: false, failed: true)])), "enable", Id);

        await Assert.That(output).Contains("Applied locally; the server has not been told yet");
    }

    [Test]
    public async Task Enable_writes_the_profile_KCAP_PROFILE_selects_and_asks_its_daemon() {
        Tmp.CreateFile("config.json", """
            { "version": 2, "active_profile": "default",
              "profiles": { "default": { "daemon": { "name": "home-daemon" } },
                            "work": { "daemon": { "name": "work-daemon" } } } }
            """);
        using var env = EnvScope.Exclusive(ProfileOverrides.ProfileVar, "work");
        var config   = new ConfigRoot(Tmp.Path);
        var profiles = await AppConfig.ResolveActiveProfile([], config, ProfileOverrides.FromEnvironment(), LocalEndpoints.None);
        var asked    = new List<string>();

        var (_, output, _) = await Run(profiles, (daemon, _) => { asked.Add(daemon); return NotRunning; }, "enable", Id);

        var store = new ExperimentalSettingsStore(config);
        await Assert.That(store.Load("work").IsEnabled(Id)).IsTrue();
        await Assert.That(store.Load("default").IsEnabled(Id)).IsFalse();
        await Assert.That(asked).IsEquivalentTo(["work-daemon"]);
        await Assert.That(output).Contains("(profile: work)");
    }

    [Test]
    public async Task List_names_the_profile() {
        var (_, output, _) = await Run(_ => NotRunning);

        await Assert.That(output).Contains("Profile: default");
    }

    [Test]
    public async Task Unknown_feature_is_refused_with_known_ids() {
        var (exit, _, error) = await Run(_ => NotRunning, "enable", "nope");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains(Id);
    }

    [Test]
    public async Task Corrupt_store_is_refused_and_left_untouched() {
        var path = Tmp.CreateFile(ExperimentalSettingsStore.FileName, "{ not json");

        var (exit, _, error) = await Run(_ => NotRunning, "enable", Id);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains(path);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task List_shows_each_registered_feature() {
        var (exit, output, _) = await Run(_ => NotRunning);

        await Assert.That(exit).IsEqualTo(0);
        foreach (var feature in ExperimentalFeatures.All)
            await Assert.That(output).Contains(feature.Id);
    }

    [Test]
    public async Task An_unknown_subcommand_prints_usage_and_fails() {
        var (exit, _, error) = await Run(_ => NotRunning, "frobnicate");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains("Usage: kcap experimental");
    }
}
