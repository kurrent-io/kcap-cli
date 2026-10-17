using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerServiceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const int Uid = 501;

    UserHome Home => new(Tmp.PathTo("home"));

    LocalServerUnit Unit => field ??= LocalServerUnit.ForInstallation(new LocalRoot(Tmp.PathTo("local")), 51234);

    /// <summary>Records every call; a command not given a result succeeds, and <c>launchctl print</c> reports the
    /// label as not loaded unless told otherwise.</summary>
    sealed class Commands {
        public List<string> Calls { get; } = [];
        public Dictionary<string, (int, string, string)> Results { get; } = [];

        /// <summary>Consulted first, for a result that changes between calls.</summary>
        public Func<string, (int, string, string)?>? Script { get; init; }

        public (int, string, string) Run(string file, string[] args) {
            var call = $"{file} {string.Join(' ', args)}";
            Calls.Add(call);

            if (Script?.Invoke(call) is { } scripted) return scripted;

            foreach (var (prefix, result) in Results)
                if (call.StartsWith(prefix, StringComparison.Ordinal)) return result;

            return args[0] == "print" ? (113, "", "Could not find service") : (0, "", "");
        }
    }

    static void NoWrite(string path, string content, Encoding? encoding = null) { }

    LocalServerLaunchdService Launchd(Commands commands, Func<LocalRoot, string?>? findLive = null, TimeProvider? time = null) =>
        new(Home, time ?? TimeProvider.System, commands.Run, NoWrite, findLive ?? (_ => null), Uid);

    LocalServerSystemdService Systemd(Commands commands, Func<LocalRoot, string?>? findLive = null, TimeProvider? time = null) =>
        new(Home, time ?? TimeProvider.System, commands.Run, NoWrite, findLive ?? (_ => null));

    string Plist => LocalServerLaunchdUnit.PlistPath(Home, Unit);

    static async Task AssertCalls(Commands commands, string[] expected) =>
        await Assert.That(string.Join("\n", commands.Calls)).IsEqualTo(string.Join("\n", expected));

    [Test]
    public async Task A_fresh_launchd_install_enables_then_loads_the_unit() {
        var commands = new Commands();

        Launchd(commands).Install(Unit);

        await AssertCalls(commands, [
            $"launchctl print gui/{Uid}/io.kurrent.kcap.local",
            $"launchctl enable gui/{Uid}/io.kurrent.kcap.local",
            $"launchctl bootstrap gui/{Uid} {Plist}",
        ]);
    }

    [Test]
    public async Task Reinstalling_over_a_loaded_unit_unloads_it_first() {
        var loaded   = true;
        var commands = new Commands {
            Script = call => call.StartsWith("launchctl print", StringComparison.Ordinal) && loaded ? (0, "state = running", "")
                           : call.StartsWith("launchctl bootout", StringComparison.Ordinal) ? Unload()
                           : null,
        };

        Launchd(commands).Install(Unit);

        await Assert.That(commands.Calls[1]).IsEqualTo($"launchctl bootout gui/{Uid}/io.kurrent.kcap.local");
        await Assert.That(commands.Calls[^1]).IsEqualTo($"launchctl bootstrap gui/{Uid} {Plist}");

        return;

        (int, string, string) Unload() {
            loaded = false;
            return (0, "", "");
        }
    }

    [Test]
    public async Task A_bootout_that_leaves_the_label_loaded_is_an_error_even_when_it_exits_zero() {
        var time = new FakeTimeProvider();
        // Each look moves the clock past the wait for the job to exit.
        var commands = new Commands {
            Script = call => {
                if (!call.StartsWith("launchctl print", StringComparison.Ordinal)) return null;
                time.Advance(TimeSpan.FromMinutes(5));
                return (0, "state = running", "");
            },
        };

        await Assert.That(() => Launchd(commands, time: time).StopAsync(Unit, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining("still Loaded");
    }

    /// <summary>bootout returns while the job is still exiting; the stop waits for launchd to drop the label.</summary>
    [Test]
    public async Task A_stop_waits_for_launchd_to_unload_the_exiting_job() {
        var looks    = 0;
        var commands = new Commands {
            Script = call => call.StartsWith("launchctl print", StringComparison.Ordinal) && ++looks < 3 ? (0, "state = running", "") : null,
        };

        await Launchd(commands).StopAsync(Unit, CancellationToken.None);

        await Assert.That(looks).IsEqualTo(3);
    }

    static readonly (int, string, string) PrintUnreadable = (5, "", "Bad request.");

    [Test]
    public async Task An_install_that_cannot_read_launchds_state_writes_nothing() {
        var written  = false;
        var commands = new Commands { Results = { ["launchctl print"] = PrintUnreadable } };
        var service  = new LocalServerLaunchdService(Home, TimeProvider.System, commands.Run, (_, _, _) => written = true, _ => null, Uid);

        await Assert.That(() => service.Install(Unit)).Throws<LocalLifecycleException>();
        await Assert.That(written).IsFalse();
    }

    /// <summary>Enabling first would clear a persisted stop for a start that then does not happen.</summary>
    [Test]
    public async Task A_start_that_cannot_read_launchds_state_leaves_the_unit_disabled() {
        var commands = new Commands { Results = { ["launchctl print"] = PrintUnreadable } };

        await Assert.That(() => Launchd(commands).Start(Unit)).Throws<LocalLifecycleException>();
        await AssertCalls(commands, [$"launchctl print gui/{Uid}/io.kurrent.kcap.local"]);
    }

    /// <summary>Disabling is what keeps a stopped stack stopped across a login, so a stop that cannot disable
    /// must not go on to unload and report success.</summary>
    [Test]
    public async Task A_launchd_stop_that_cannot_disable_changes_nothing() {
        var commands = new Commands { Results = { ["launchctl disable"] = (1, "", "denied") } };

        await Assert.That(() => Launchd(commands).StopAsync(Unit, CancellationToken.None)).Throws<LocalLifecycleException>();
        await AssertCalls(commands, [$"launchctl disable gui/{Uid}/io.kurrent.kcap.local"]);
    }

    [Test]
    public async Task A_launchd_stop_disables_unloads_and_waits_for_the_stack() {
        var commands = new Commands();
        var probes   = 0;

        await Launchd(commands, findLive: _ => ++probes < 3 ? "Postgres still accepts connections" : null)
            .StopAsync(Unit, CancellationToken.None);

        await AssertCalls(commands, [
            $"launchctl disable gui/{Uid}/io.kurrent.kcap.local",
            $"launchctl bootout gui/{Uid}/io.kurrent.kcap.local",
            $"launchctl print gui/{Uid}/io.kurrent.kcap.local",
        ]);
        await Assert.That(probes).IsEqualTo(3);
    }

    [Test]
    public async Task A_stop_fails_when_part_of_the_stack_outlives_the_service() {
        var time = new FakeTimeProvider();

        // Each probe moves the clock past the budget, so the stop gives up on the first look.
        string FindLive(LocalRoot _) {
            time.Advance(TimeSpan.FromMinutes(5));
            return "the server still holds its instance lock";
        }

        await Assert.That(() => Launchd(new Commands(), FindLive, time).StopAsync(Unit, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining("instance lock");
    }

    [Test]
    public async Task A_bootout_failure_is_fine_when_the_label_is_already_gone() {
        var commands = new Commands { Results = { ["launchctl bootout"] = (3, "", "No such process") } };

        await Launchd(commands).StopAsync(Unit, CancellationToken.None);

        await Assert.That(commands.Calls[^1]).IsEqualTo($"launchctl print gui/{Uid}/io.kurrent.kcap.local");
    }

    [Test]
    public async Task Launchd_start_kickstarts_a_loaded_unit_and_bootstraps_an_unloaded_one() {
        var loaded = new Commands { Results = { ["launchctl print"] = (0, "state = not running", "") } };
        Launchd(loaded).Start(Unit);

        var unloaded = new Commands();
        Launchd(unloaded).Start(Unit);

        await Assert.That(loaded.Calls[^1]).IsEqualTo($"launchctl kickstart gui/{Uid}/io.kurrent.kcap.local");
        await Assert.That(unloaded.Calls[^1]).IsEqualTo($"launchctl bootstrap gui/{Uid} {Plist}");
    }

    [Test]
    public async Task A_systemd_install_starts_the_unit_exactly_once() {
        var commands = new Commands();

        Systemd(commands).Install(Unit);

        await AssertCalls(commands, [
            "systemctl --user daemon-reload",
            "systemctl --user enable kcap-local.service",
            "systemctl --user reset-failed kcap-local.service",
            "systemctl --user restart kcap-local.service",
        ]);
    }

    /// <summary>A unit that hit its start limit refuses every start until the limit is cleared.</summary>
    [Test]
    public async Task A_systemd_start_clears_a_hit_start_limit_first() {
        var commands = new Commands { Results = { ["systemctl --user reset-failed"] = (1, "", "Unit kcap-local.service not loaded.") } };

        Systemd(commands).Start(Unit);

        await AssertCalls(commands, [
            "systemctl --user reset-failed kcap-local.service",
            "systemctl --user enable --now kcap-local.service",
        ]);
    }

    /// <summary>A server that has not taken its lock yet passes the stack probe, so the stop's own result must
    /// decide.</summary>
    [Test]
    public async Task A_failed_stop_of_a_unit_whose_file_is_gone_is_an_error_even_with_nothing_alive() {
        var commands = new Commands { Results = { ["systemctl --user stop"] = (1, "", "Job for kcap-local.service canceled.") } };

        await Assert.That(() => Systemd(commands).UninstallAsync(Unit, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining("canceled");
    }

    [Test]
    public async Task A_unit_systemd_has_no_record_of_needs_no_stop() {
        var commands = new Commands { Results = { ["systemctl --user stop"] = (5, "", "Failed to stop kcap-local.service: Unit kcap-local.service not loaded.") } };

        await Systemd(commands).UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(commands.Calls[^1]).IsEqualTo("systemctl --user daemon-reload");
    }

    [Test]
    public async Task A_systemd_uninstall_still_stops_a_unit_whose_file_is_gone() {
        var commands = new Commands();
        var probed   = false;

        await Systemd(commands, findLive: _ => { probed = true; return null; }).UninstallAsync(Unit, CancellationToken.None);

        await AssertCalls(commands, [
            "systemctl --user stop kcap-local.service",
            "systemctl --user daemon-reload",
        ]);
        await Assert.That(probed).IsTrue();
    }

    [Test]
    public async Task A_failed_systemd_disable_is_an_error() {
        var commands = new Commands { Results = { ["systemctl --user disable"] = (1, "", "Failed to disable unit") } };
        var probed   = false;

        await Assert.That(() => Systemd(commands, findLive: _ => { probed = true; return null; }).StopAsync(Unit, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining("Failed to disable unit");
        await Assert.That(probed).IsFalse();
    }

    [Test]
    public async Task A_systemd_uninstall_stops_removes_the_unit_and_reloads() {
        var unitPath = Tmp.CreateFile(["home", ".config", "systemd", "user", "kcap-local.service"], "[Unit]\n");
        var commands = new Commands();

        await Systemd(commands).UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(File.Exists(unitPath)).IsFalse();
        await AssertCalls(commands, [
            "systemctl --user disable --now kcap-local.service",
            "systemctl --user daemon-reload",
        ]);
    }
}
