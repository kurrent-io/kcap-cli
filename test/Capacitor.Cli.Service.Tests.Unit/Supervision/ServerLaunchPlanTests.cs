using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

/// Windows path semantics: the service runs nowhere else
[RunOn(OS.Windows)]
public class ServerLaunchPlanTests {
    static readonly ServerEnrollment Local = new(
        @"C:\Users\alice\.config\kcap\local\bin\current\kcap-server.exe", @"C:\Users\alice\.config\kcap\local",
        @"C:\Users\alice\AppData\Local\kcap\current\bin\kcap.exe", @"C:\Users\alice\.config\kcap\local\logs\server.log");

    [Test]
    public async Task The_server_runs_from_its_own_directory_and_logs_to_the_enrolled_file() {
        var plan = ServerLaunchPlan.For(Local, new Dictionary<string, string> { ["PATH"] = @"C:\tools" });

        await Assert.That(plan.CommandLine).IsEqualTo("\"C:\\Users\\alice\\.config\\kcap\\local\\bin\\current\\kcap-server.exe\"");
        await Assert.That(plan.WorkingDirectory).IsEqualTo(@"C:\Users\alice\.config\kcap\local\bin\current");
        await Assert.That(plan.LogPath).IsEqualTo(Local.LogFile);
        await Assert.That(plan.Environment["PATH"]).IsEqualTo(@"C:\tools");
    }

    [Test]
    public async Task The_environment_file_wins_over_the_logon_environment() {
        var logon    = new Dictionary<string, string> { ["PATH"] = @"C:\Windows", ["TEMP"] = @"C:\t" };
        var captured = new Dictionary<string, string> { ["path"] = @"C:\nvm;C:\Windows", ["KCAP_LOCAL_PORT"] = "5180" };

        var plan = ServerLaunchPlan.For(Local, logon, captured);

        await Assert.That(plan.Environment["PATH"]).IsEqualTo(@"C:\nvm;C:\Windows");
        await Assert.That(plan.Environment["TEMP"]).IsEqualTo(@"C:\t");
        await Assert.That(plan.Environment["KCAP_LOCAL_PORT"]).IsEqualTo("5180");
        await Assert.That(plan.EnvironmentBlock()).IsEqualTo("KCAP_LOCAL_PORT=5180\0PATH=C:\\nvm;C:\\Windows\0TEMP=C:\\t\0\0");
    }

    [Test]
    public async Task The_stop_runs_the_users_own_cli_to_break_into_the_server() {
        await Assert.That(ServerLaunchPlan.StopCommandLine(Local, 4242)).IsEqualTo(
            "\"C:\\Users\\alice\\AppData\\Local\\kcap\\current\\bin\\kcap.exe\" local console-break 4242");
    }
}
