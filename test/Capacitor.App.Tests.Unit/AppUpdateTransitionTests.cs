using System.Text.Json.Nodes;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class AppUpdateTransitionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_saved_handover_is_taken_once() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        var saved = AppUpdateTransition.Begin(AppUpdateTransition.InApp, "1.4.0", "1.4.1", clock);
        saved.Save(config);
        clock.Advance(TimeSpan.FromSeconds(9));

        var taken = AppUpdateTransition.Take(config, clock);

        await Assert.That(taken).IsEqualTo(saved);
        await Assert.That(AppUpdateTransition.Take(config, clock)).IsNull();
    }

    [Test]
    public async Task A_handover_no_process_picked_up_in_time_is_dropped() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        AppUpdateTransition.Begin(AppUpdateTransition.Installer, "1.4.0", "1.4.1", clock).Save(config);
        clock.Advance(AppUpdateTransition.Freshness);

        await Assert.That(AppUpdateTransition.Take(config, clock)).IsNull();
        await Assert.That(File.Exists(config.Path(AppUpdateTransition.FileName))).IsFalse();
    }

    [Test]
    public async Task A_corrupt_record_is_dropped() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        File.WriteAllText(config.Path(AppUpdateTransition.FileName), "{ not json");

        await Assert.That(AppUpdateTransition.Take(config, new FakeTimeProvider())).IsNull();
        await Assert.That(File.Exists(config.Path(AppUpdateTransition.FileName))).IsFalse();
    }

    [Test]
    [Arguments("1.4.1", "1.4.1+abc123", true)]
    [Arguments("1.4.1", "1.4.0", false)]
    [Arguments("1.5.0-beta.2", "1.5.0-beta.2", true)]
    public async Task Applied_compares_versions_without_build_metadata(string to, string running, bool applied) {
        var transition = new AppUpdateTransition("x", AppUpdateTransition.InApp, null, to, DateTimeOffset.UnixEpoch);

        await Assert.That(transition.AppliedTo(running)).IsEqualTo(applied);
    }

    [Test]
    public async Task The_component_stamp_is_owed_until_written_for_this_version() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));

        await Assert.That(AppComponentStamp.Owed(config, "1.4.1")).IsTrue();
        AppComponentStamp.Write(config, "1.4.0");
        await Assert.That(AppComponentStamp.Owed(config, "1.4.1")).IsTrue();
        AppComponentStamp.Write(config, "1.4.1");
        await Assert.That(AppComponentStamp.Owed(config, "1.4.1")).IsFalse();
    }

    [Test]
    public async Task Only_an_upgrade_that_finished_or_had_nothing_to_do_settles_the_components() {
        await Assert.That(AppComponentStamp.SettledBy(new LocalUpgradeOutcome.Upgraded("1.4.0", "1.4.1"))).IsTrue();
        await Assert.That(AppComponentStamp.SettledBy(new LocalUpgradeOutcome.NothingToDo("current"))).IsTrue();
        await Assert.That(AppComponentStamp.SettledBy(new LocalUpgradeOutcome.NotPublished("not yet"))).IsFalse();
        await Assert.That(AppComponentStamp.SettledBy(new LocalUpgradeOutcome.Failed(
            "start_failed", "did not start", true, LocalInstallAffordance.For("start_failed"), "1.4.0", "1.4.1"))).IsFalse();
    }

    [Test]
    public async Task The_lifecycle_log_writes_one_bounded_object_per_line_with_the_home_folded() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var log = new LifecycleLog(config, new FakeTimeProvider(), "/Users/example");

        log.Write("install_started", ("source", "/Users/example/Downloads/kcap.app"), ("error", new string('x', 900)), ("skipped", null));
        log.Write("install_completed");

        var lines = File.ReadAllLines(log.Path);
        var first = JsonNode.Parse(lines[0])!;
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(first["event"]!.GetValue<string>()).IsEqualTo("install_started");
        await Assert.That(first["source"]!.GetValue<string>()).IsEqualTo("~/Downloads/kcap.app");
        await Assert.That(first["error"]!.GetValue<string>().Length).IsEqualTo(501);
        await Assert.That(first["skipped"]).IsNull();
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(log.Path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task The_lifecycle_log_rotates_once_past_its_bound_and_never_writes_once_closed() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var closed = false;
        var log = new LifecycleLog(config, new FakeTimeProvider(), "/Users/example", () => closed);
        File.WriteAllText(log.Path, new string('x', (int)LifecycleLog.MaxBytes));

        log.Write("app_started");
        closed = true;
        log.Write("app_exiting");

        await Assert.That(File.Exists(log.Path + ".1")).IsTrue();
        await Assert.That(File.ReadAllLines(log.Path).Single()).Contains("app_started");
    }
}
