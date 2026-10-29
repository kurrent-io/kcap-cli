using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The version floor app-server needs, failing toward PTY on anything that is not a clean
/// release at or above it.</summary>
public class CodexTransportDecisionTests {
    [Test]
    [Arguments("0.146.0", true)]
    [Arguments("0.147.0", true)]
    [Arguments("1.2.0", true)]
    [Arguments("0.145.0", false)]
    [Arguments("0.99.0", false)]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("not-a-version", false)]
    [Arguments("0.146.0-rc.1", false)] // a prerelease is below the verified release
    [Arguments("0.147.0-rc.1", false)] // a prerelease is unverified even above the floor
    [Arguments("0.146.0+meta", false)]
    [Arguments("0.146.x", false)]
    [Arguments("codex-cli 0.146.0", true)]
    [Arguments("v0.146.0", true)]
    public async Task MeetsFloor_truth_table(string? version, bool expected) =>
        await Assert.That(CodexTransportDecision.MeetsFloor(version)).IsEqualTo(expected);

    [Test]
    public async Task MeetsFloor_is_exactly_the_pinned_floor() {
        await Assert.That(CodexTransportDecision.MeetsFloor(CodexTransportDecision.VersionFloor)).IsTrue();
        await Assert.That(CodexTransportDecision.MeetsFloor("0.146.1")).IsTrue();
        await Assert.That(CodexTransportDecision.MeetsFloor("0.146")).IsTrue(); // patch defaults to 0
        await Assert.That(CodexTransportDecision.MeetsFloor("0.145.99")).IsFalse();
    }

    [Test]
    [Arguments("codex", null, false)]
    [Arguments("codex", "app-server", false)]
    [Arguments("codex", "pty", false)]
    [Arguments("codex", "tty", true)]
    [Arguments("codex", "App-Server", true)]
    [Arguments("claude", "tty", false)]
    public async Task Only_an_unrecognised_codex_expectation_is_rejected(string vendor, string? expected, bool rejected) =>
        await Assert.That(CodexTransportDecision.ExpectedTransportRejection(vendor, expected) is not null).IsEqualTo(rejected);
}
