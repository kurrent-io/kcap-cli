using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>daemon stop</c> refuses a daemon a service manager supervises, since the manager would relaunch
/// it, and the refusal exits nonzero so a script does not read the still-running daemon as stopped.</summary>
public class DaemonStopSupervisedTests {
    static readonly ShutdownRequestDto End  = new("end", "final");
    static readonly ShutdownRequestDto Keep = new("keep", "final");

    [Test]
    public async Task A_supervised_daemon_is_refused_with_a_nonzero_exit_and_the_service_command() {
        var manager = new RecordingServiceManager(new() { ["sandbox"] = "/opt/kcap/kcap-daemon" });
        var error   = new StringWriter();

        var exit = DaemonCommands.RefuseSupervisedStop(manager, "sandbox", End, error);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error.ToString()).Contains("is managed by fake; a raw stop would be auto-restarted.");
        await Assert.That(error.ToString()).Contains("Use: kcap daemon service stop --name sandbox  (or uninstall");
        await Assert.That(manager.Stopped).IsEmpty();
    }

    /// <summary>The service verbs resolve a name through the same sanitizing, so the suggestion names the id
    /// they will find, in a form a shell takes unquoted.</summary>
    [Test]
    public async Task The_suggested_command_names_the_service_id_and_keeps_the_agents_when_asked() {
        var manager = new RecordingServiceManager(new() { ["my-laptop"] = "/opt/kcap/kcap-daemon" });
        var error   = new StringWriter();

        var exit = DaemonCommands.RefuseSupervisedStop(manager, "My Laptop", Keep, error);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error.ToString()).Contains("kcap daemon service stop --name my-laptop --keep-agents");
    }

    [Test]
    public async Task An_unsupervised_daemon_is_not_refused() {
        var manager = new RecordingServiceManager(new() { ["other"] = "/opt/kcap/kcap-daemon" });
        var error   = new StringWriter();

        await Assert.That(DaemonCommands.RefuseSupervisedStop(manager, "sandbox", End, error)).IsNull();
        await Assert.That(DaemonCommands.RefuseSupervisedStop(null, "sandbox", End, error)).IsNull();
        await Assert.That(error.ToString()).IsEmpty();
    }
}
