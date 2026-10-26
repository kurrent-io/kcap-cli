using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexTransportResolutionTests {
    [Test]
    public async Task Resolve_does_not_probe_when_the_switch_is_off() {
        var probes = 0;

        var resolution = CodexTransportResolution.Resolve(3, switchOn: false, () => { probes++; return "0.146.0"; });

        await Assert.That(probes).IsEqualTo(0);
        await Assert.That(resolution).IsEqualTo(new CodexTransportResolution(3, false, null, false, null));
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.Pty);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_active_at_the_floor() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0");

        await Assert.That(resolution).IsEqualTo(new CodexTransportResolution(1, true, "0.146.0", true, null));
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.AppServer);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexAppServerLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_inactive_below_the_floor_with_a_reason() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.145.9");

        await Assert.That(resolution.Active).IsFalse();
        await Assert.That(resolution.CliVersion).IsEqualTo("0.145.9");
        await Assert.That(resolution.InactiveReason).IsEqualTo("Codex 0.145.9 is below 0.146.0");
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_inactive_when_the_version_is_unreadable() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => null);

        await Assert.That(resolution.Active).IsFalse();
        await Assert.That(resolution.InactiveReason).IsEqualTo("Codex version could not be read");
    }
}
