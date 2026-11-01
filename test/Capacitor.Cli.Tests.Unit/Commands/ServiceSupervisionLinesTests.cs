using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ServiceSupervisionLinesTests {
    static string[] Lines(ServicePipeStatus? daemon, ServicePipeStatus? server = null, bool? serverStopped = null) =>
        [.. ServiceSupervisionLines.For("laptop", daemon, server, serverStopped)];

    /// A daemon that outlived a service restart has the old service process as its parent; the line is where the
    /// user learns the new one supervises it
    [Test]
    public async Task An_adopted_daemon_is_said_to_be_supervised() {
        var lines = Lines(new ServicePipeStatus("running", Pid: 2908, Adopted: true, HasJob: true));

        await Assert.That(lines.Single()).IsEqualTo(
            "  Supervisor: kcap-service adopted this daemon (pid 2908), already running when the service started, and restarts it if it crashes.");
    }

    [Test]
    public async Task An_adopted_daemon_outside_the_job_says_what_a_forced_stop_leaves() {
        var lines = Lines(new ServicePipeStatus("running", Pid: 2908, Adopted: true));

        await Assert.That(lines.Single()).Contains("ends the daemon but not its agents");
    }

    [Test]
    public async Task A_launched_daemon_and_a_running_local_server_each_get_a_line() {
        var lines = Lines(
            new ServicePipeStatus("running", Pid: 2908, HasJob: true), new ServicePipeStatus("running", Pid: 6284), serverStopped: false);

        await Assert.That(lines).IsEquivalentTo([
            "  Supervisor: kcap-service started this daemon (pid 2908) and restarts it if it crashes.",
            "  Local server: kcap-service runs it too (pid 6284).",
        ]);
    }

    [Test]
    public async Task A_stopped_local_server_names_the_command_that_starts_it() {
        var lines = Lines(null, new ServicePipeStatus("no_session"), serverStopped: true);

        await Assert.That(lines.Single()).IsEqualTo("  Local server: kcap-service runs it too; it stays stopped until `kcap local start`.");
    }

    [Test]
    public async Task No_local_server_enrollment_adds_no_server_line() {
        var lines = Lines(new ServicePipeStatus("stopped"), new ServicePipeStatus("no_session"));

        await Assert.That(lines.Single()).IsEqualTo(
            "  Supervisor: kcap-service keeps this daemon stopped until `kcap daemon service start --name laptop`.");
    }
}
