using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[RunOn(OS.Linux)]
public class LocalServerServicesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UserHome Home => new(Tmp.PathTo("home"));

    /// <summary>Uninstall reads an installation it cannot query as installed, so it keeps its data instead of
    /// deleting it with the config directory.</summary>
    [Test]
    public async Task Linux_without_systemd_reads_the_unit_as_unknown_and_refuses_to_change_it() {
        var service = LocalServerServices.ForCurrentOs(Home, TimeProvider.System, systemdRunning: () => false);
        var unit    = LocalServerUnit.ForInstallation(LocalRoot.Resolve(Tmp.PathTo("local"), Home), 4100);

        await Assert.That(service.Owner(unit).Loaded).IsTypeOf<OwnerReading.Unreadable>();
        await Assert.That(service.Activity(unit)).IsTypeOf<UnitActivity.Unknown>();
        await Assert.That(() => service.Start(unit)).Throws<LocalLifecycleException>().WithMessageContaining("systemd is not running");
    }

    [Test]
    public async Task Linux_with_systemd_is_the_systemd_service() {
        await Assert.That(LocalServerServices.ForCurrentOs(Home, TimeProvider.System, systemdRunning: () => true))
            .IsTypeOf<LocalServerSystemdService>();
    }
}
