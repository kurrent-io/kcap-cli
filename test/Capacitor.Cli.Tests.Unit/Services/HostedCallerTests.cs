using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Whether a service command would be ended by the daemon it replaces: the launch marker of that
/// daemon, or that daemon as an ancestor, each says so on its own.
/// </summary>
public class HostedCallerTests {
    static Func<string, string?> Env(string? daemonId) => name => name == HostedAgent.DaemonIdVar ? daemonId : null;

    [Test]
    public async Task The_targets_launch_marker_means_hosted() {
        await Assert.That(HostedCaller.HostedBy("tony", Env(HostedAgent.DaemonId("tony")), daemonPid: null, _ => false)).IsTrue();
    }

    /// <summary>An agent of another daemon is not ended by replacing this one.</summary>
    [Test]
    public async Task Another_daemons_marker_is_not_hosted() {
        await Assert.That(HostedCaller.HostedBy("tony", Env(HostedAgent.DaemonId("other")), daemonPid: 42, _ => false)).IsFalse();
        await Assert.That(HostedCaller.HostedBy("tony", Env(""), daemonPid: null, _ => false)).IsFalse();
    }

    /// <summary>An ACP agent carries the lookup markers instead of the id; a name exported on its own,
    /// as a user does to pick a daemon, is not one.</summary>
    [Test]
    public async Task The_lookup_markers_naming_the_target_mean_hosted() {
        Func<string, string?> Lookup(string? agentId, string name) => key =>
            key == HostedAgent.DaemonAgentIdVar ? agentId : key == DaemonNameResolver.NameVar ? name : null;

        await Assert.That(HostedCaller.HostedBy("tony", Lookup("a1", "tony"), daemonPid: null, _ => false)).IsTrue();
        await Assert.That(HostedCaller.HostedBy("tony", Lookup("a1", "other"), daemonPid: null, _ => false)).IsFalse();
        await Assert.That(HostedCaller.HostedBy("tony", Lookup(null, "tony"), daemonPid: null, _ => false)).IsFalse();
    }

    /// <summary>Some agents carry no marker; the running daemon above this process is enough.</summary>
    [Test]
    public async Task The_running_daemon_as_an_ancestor_means_hosted() {
        await Assert.That(HostedCaller.HostedBy("tony", Env(null), daemonPid: 42, pid => pid == 42)).IsTrue();
        await Assert.That(HostedCaller.HostedBy("tony", Env(null), daemonPid: 42, _ => false)).IsFalse();
    }

    [Test]
    public async Task The_refusal_names_the_daemon_and_the_command_to_run_elsewhere() {
        var text = HostedCaller.Refusal("tony", "`kcap daemon service install --replace --verify --name tony`");

        await Assert.That(text).Contains("daemon 'tony' hosts");
        await Assert.That(text).Contains("nothing was changed");
        await Assert.That(text).EndsWith("`kcap daemon service install --replace --verify --name tony`");
    }
}
