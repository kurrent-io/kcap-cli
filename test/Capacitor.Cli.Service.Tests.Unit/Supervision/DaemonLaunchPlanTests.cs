using Capacitor.Cli.Service.Sessions;
using TUnit.Core.Enums;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

/// Windows path semantics: the service runs nowhere else
[RunOn(OS.Windows)]
public class DaemonLaunchPlanTests {
    const string Daemon = @"C:\Users\alice\AppData\Local\kcap\current\kcap-daemon.exe";
    const string Profile = @"C:\Users\alice";

    [Test]
    public async Task The_daemon_is_named_supervised_and_logs_under_the_users_config_directory() {
        var plan = DaemonLaunchPlan.For(new Enrollment("laptop", Daemon, null, null), Profile, new Dictionary<string, string> { ["PATH"] = @"C:\tools" });

        await Assert.That(plan.CommandLine).IsEqualTo(
            $"\"{Daemon}\" --name \"laptop\" --log-file \"C:\\Users\\alice\\.config\\kcap\\daemon-laptop.log\"");
        await Assert.That(plan.Environment[DaemonLaunchPlan.SupervisedVariable]).IsEqualTo("laptop");
        await Assert.That(plan.Environment["PATH"]).IsEqualTo(@"C:\tools");
        await Assert.That(plan.StderrPath).IsEqualTo(@"C:\Users\alice\.config\kcap\daemon-laptop.stderr.log");
    }

    [Test]
    public async Task A_custom_config_directory_profile_and_agent_cap_carry_through() {
        var env = new Dictionary<string, string> { ["KCAP_CONFIG_DIR"] = @"D:\kcap" };
        var plan = DaemonLaunchPlan.For(new Enrollment("laptop", Daemon, "work", 4), Profile, env);

        await Assert.That(plan.LogPath).IsEqualTo(@"D:\kcap\daemon-laptop.log");
        await Assert.That(plan.CommandLine).EndsWith(" --max-agents 4");
        await Assert.That(plan.Environment["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task A_supervised_marker_the_user_exported_is_replaced_with_the_enrolled_name() {
        var env = new Dictionary<string, string> { [DaemonLaunchPlan.SupervisedVariable] = "other" };

        var plan = DaemonLaunchPlan.For(new Enrollment("laptop", Daemon, null, null), Profile, env);

        await Assert.That(plan.Environment[DaemonLaunchPlan.SupervisedVariable]).IsEqualTo("laptop");
    }

    [Test]
    public async Task The_captured_shell_environment_wins_over_the_logon_one_but_not_over_the_enrollment() {
        var logon    = new Dictionary<string, string> { ["PATH"] = @"C:\Windows", ["TEMP"] = @"C:\t" };
        var captured = new Dictionary<string, string> {
            ["PATH"] = @"C:\nvm;C:\Windows", ["KCAP_PROFILE"] = "shell", [DaemonLaunchPlan.SupervisedVariable] = "other",
        };

        var plan = DaemonLaunchPlan.For(new Enrollment("laptop", Daemon, "work", null), Profile, logon, captured);

        await Assert.That(plan.Environment["PATH"]).IsEqualTo(@"C:\nvm;C:\Windows");
        await Assert.That(plan.Environment["TEMP"]).IsEqualTo(@"C:\t");
        await Assert.That(plan.Environment["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(plan.Environment[DaemonLaunchPlan.SupervisedVariable]).IsEqualTo("laptop");
    }

    [Test]
    public async Task The_environment_block_is_sorted_and_double_terminated() {
        var plan = DaemonLaunchPlan.For(new Enrollment("n", Daemon, null, null), Profile, new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" });

        await Assert.That(plan.EnvironmentBlock()).IsEqualTo("A=1\0b=2\0KCAP_DAEMON_SUPERVISED=n\0\0");
    }

    [Test]
    public async Task The_stop_runs_the_users_own_cli_past_the_managed_refusal() {
        await Assert.That(DaemonLaunchPlan.StopCommandLine(new Enrollment("laptop", Daemon, null, null))).IsEqualTo(
            "\"C:\\Users\\alice\\AppData\\Local\\kcap\\current\\kcap.exe\" daemon stop --name \"laptop\" --direct");
    }
}
