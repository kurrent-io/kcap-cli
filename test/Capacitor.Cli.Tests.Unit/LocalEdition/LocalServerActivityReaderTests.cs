using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerActivityReaderTests {
    [Test]
    public async Task An_unknown_label_is_stopped() =>
        await Assert.That(LocalServerActivityReader.FromPrint(113, "", "Could not find service \"io.kurrent.kcap.local\" in domain"))
            .IsEqualTo(UnitActivity.Stopped.Instance);

    [Test]
    public async Task A_failed_print_is_unknown() =>
        await Assert.That(LocalServerActivityReader.FromPrint(1, "", "Operation not permitted")).IsTypeOf<UnitActivity.Unknown>();

    [Test]
    public async Task A_job_with_a_pid_is_running() =>
        await Assert.That(LocalServerActivityReader.FromPrint(0, "\tstate = running\n\tpid = 4242\n\tlast exit code = (never exited)\n", ""))
            .IsEqualTo(new UnitActivity.Running(4242));

    [Test]
    public async Task A_loaded_job_that_failed_is_restarting_with_its_code() =>
        await Assert.That(LocalServerActivityReader.FromPrint(0, "\tstate = not running\n\tlast exit code = 78: EX_CONFIG\n", ""))
            .IsEqualTo(new UnitActivity.Restarting(78));

    [Test]
    public async Task A_loaded_job_that_exited_cleanly_is_not_restarted() =>
        await Assert.That(LocalServerActivityReader.FromPrint(0, "\tstate = not running\n\tlast exit code = 0\n", ""))
            .IsEqualTo(new UnitActivity.Exited(0));

    [Test]
    public async Task A_loaded_job_not_yet_spawned_is_restarting_without_a_code() =>
        await Assert.That(LocalServerActivityReader.FromPrint(0, "\tstate = spawn scheduled\n\tlast exit code = (never exited)\n", ""))
            .IsEqualTo(new UnitActivity.Restarting(null));

    [Test]
    public async Task Launchd_runs_and_systemd_restarts_are_the_start_count() {
        await Assert.That(LocalServerActivityReader.FromPrint(0, "\truns = 7\n\tpid = 1\n", "").Starts).IsEqualTo(7);
        await Assert.That(LocalServerActivityReader.FromShow(0, "ActiveState=active\nMainPID=9\nNRestarts=4\n", "").Starts).IsEqualTo(4);
    }

    [Test]
    [Arguments("ActiveState=active\nSubState=running\nMainPID=99\nExecMainStatus=0\n", "running")]
    [Arguments("ActiveState=activating\nSubState=auto-restart\nMainPID=0\nExecMainStatus=1\n", "restarting")]
    [Arguments("ActiveState=failed\nSubState=failed\nMainPID=0\nExecMainStatus=1\n", "exited")]
    [Arguments("ActiveState=inactive\nSubState=dead\nMainPID=0\nExecMainStatus=0\n", "stopped")]
    public async Task Systemd_states_map_to_activity(string show, string expected) {
        var activity = LocalServerActivityReader.FromShow(0, show, "");

        var name = activity switch {
            UnitActivity.Running    => "running",
            UnitActivity.Restarting => "restarting",
            UnitActivity.Exited     => "exited",
            UnitActivity.Stopped    => "stopped",
            _                       => "unknown",
        };

        await Assert.That(name).IsEqualTo(expected);
    }
}
