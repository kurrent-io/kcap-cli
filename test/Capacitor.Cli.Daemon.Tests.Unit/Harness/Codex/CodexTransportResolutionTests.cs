using System.Collections.Frozen;
using Capacitor.Cli.Core.Config;
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

    static readonly ExperimentalSettingsRead On =
        new(true, new Dictionary<string, bool> { ["codex-app-server"] = true }.ToFrozenDictionary(), null);

    static readonly ExperimentalSettingsRead Broken = new(false, FrozenDictionary<string, bool>.Empty, "experimental.json is not valid JSON");

    static readonly CodexTransportResolution Kept = new(4, true, "0.146.0", true, null);

    /// <summary>Whichever read fails, before the probe or after it, the pass answers the fallback and
    /// never a switch-off resolution.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Read_answers_the_fallback_when_a_read_fails(int failingRead) {
        var reads = 0;
        string? problem = null;

        var resolution = CodexTransportResolution.Read(5, () => reads++ == failingRead ? Broken : On, () => "0.146.0",
            p => { problem = p; return Kept; });

        await Assert.That(ReferenceEquals(resolution, Kept)).IsTrue();
        await Assert.That(problem).IsEqualTo("experimental.json is not valid JSON");
    }
}
