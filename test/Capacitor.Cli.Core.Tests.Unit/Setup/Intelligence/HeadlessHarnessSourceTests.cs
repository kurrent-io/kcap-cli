using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>A connected daemon advertising headless/1 is the authority; anything short of that falls back to the probe</summary>
public class HeadlessHarnessSourceTests {
    static readonly string[] Probed = ["claude", "codex"];

    static ProbeResult Daemon(string connection = "connected", string[]? headless = null, bool capability = true) =>
        new(true,
            new HelloReplyDto(1, "1.0", "d", capability ? [HeadlessStatus.Capability] : ["status/1"]),
            new DaemonStatusDto(new DaemonInfoDto("d", "1.0", "http://x", connection, 4, 0, HeadlessVendors: headless), []),
            true);

    [Test]
    public async Task A_connected_daemon_reports_its_own_list() =>
        await Assert.That(HeadlessHarnessSource.Choose(Daemon(headless: ["codex"]), () => Probed)).IsEquivalentTo(["codex"]);

    [Test]
    public async Task An_empty_list_from_a_connected_daemon_stands() =>
        await Assert.That(HeadlessHarnessSource.Choose(Daemon(headless: []), () => Probed)).IsEmpty();

    [Test]
    public async Task A_daemon_not_yet_connected_defers_to_the_probe() =>
        await Assert.That(HeadlessHarnessSource.Choose(Daemon("connecting", ["codex"]), () => Probed)).IsEquivalentTo(Probed);

    [Test]
    public async Task A_daemon_without_the_capability_defers_to_the_probe() =>
        await Assert.That(HeadlessHarnessSource.Choose(Daemon(headless: ["codex"], capability: false), () => Probed)).IsEquivalentTo(Probed);

    [Test]
    public async Task No_daemon_defers_to_the_probe() =>
        await Assert.That(HeadlessHarnessSource.Choose(new ProbeResult(false, null, null, false), () => Probed)).IsEquivalentTo(Probed);
}
