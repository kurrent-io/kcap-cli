using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`start --verify` on systemd runs the verify engine, which reads the unit through systemctl.</summary>
public class DaemonCommandsServiceStartTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test, NotInParallel]
    public async Task Verify_on_systemd_reports_a_unit_that_is_not_installed() {
        using var err = ConsoleOutput.StartErrorCapture();
        var systemd = new SystemdServiceManager(Home, runProcess: (_, _) => (0, "ActiveState=inactive\nUnitFileState=\nMainPID=0\n", ""));

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), systemd, "test-id", Home, TimeProvider.System).Start(["--verify"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(err.GetCapturedError()).Contains("Service 'test-id' is not installed.");
    }
}
