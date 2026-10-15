using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`start --verify` needs a manager that reports the job's own pid — launchd or a Windows
/// Scheduled Task — so systemd gets a clear rejection rather than a transaction it cannot verify.</summary>
public class DaemonCommandsServiceStartTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Verify_flag_is_rejected_on_a_non_launchd_manager() {
        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), new SystemdServiceManager(Home), "test-id", Home, TimeProvider.System).Start(["--verify"]);
        await Assert.That(exit).IsEqualTo(1);
    }
}
