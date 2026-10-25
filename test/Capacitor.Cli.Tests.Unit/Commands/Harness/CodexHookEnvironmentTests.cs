using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The two markers are read by the names the runners and the daemon export, and only "1" sets one.
public class CodexHookEnvironmentTests {
    [Test]
    [Arguments("1", true)]
    [Arguments("0", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Each_marker_is_set_only_by_1(string? value, bool expected) {
        var env = CodexHookEnvironment.FromProcess(k => k is "KCAP_SKIP" or "KCAP_HOSTED_APPSERVER" ? value : null);

        await Assert.That(env.Skip).IsEqualTo(expected);
        await Assert.That(env.HostedAppServer).IsEqualTo(expected);
    }

    [Test]
    public async Task The_markers_are_read_independently() {
        var skipOnly = CodexHookEnvironment.FromProcess(k => k is "KCAP_SKIP" ? "1" : null);

        await Assert.That(skipOnly.Skip).IsTrue();
        await Assert.That(skipOnly.HostedAppServer).IsFalse();
    }
}
