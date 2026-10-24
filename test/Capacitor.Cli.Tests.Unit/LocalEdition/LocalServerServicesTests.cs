using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[RunOn(OS.Linux)]
public class LocalServerServicesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UserHome Home => new(Tmp.PathTo("home"));

    /// <summary>Uninstall and upgrade read this code as "nothing installed".</summary>
    [Test]
    public async Task Linux_without_systemd_is_an_unsupported_platform() {
        var ex = await Assert.That(() => LocalServerServices.ForCurrentOs(Home, TimeProvider.System, systemdRunning: () => false))
            .Throws<LocalInstallFailure>();

        await Assert.That(ex!.Code).IsEqualTo(LocalInstallFailureCode.UnsupportedPlatform);
    }

    [Test]
    public async Task Linux_with_systemd_is_the_systemd_service() {
        await Assert.That(LocalServerServices.ForCurrentOs(Home, TimeProvider.System, systemdRunning: () => true))
            .IsTypeOf<LocalServerSystemdService>();
    }
}
