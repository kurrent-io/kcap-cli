using Capacitor.Cli.Service.Installation;
using static Capacitor.Cli.Service.Installation.InstallStep;

namespace Capacitor.Cli.Service.Tests.Unit.Installation;

public class InstallPlanTests {
    [Test]
    public async Task A_first_install_copies_registers_and_starts() =>
        await Assert.That(InstallPlan.For(serviceExists: false, binaryCurrent: false, running: false))
            .IsEquivalentTo([PrepareDirectory, CopyBinary, CreateService, StartService]);

    /// The stop comes before the copy: the running image cannot be replaced, and the daemons it
    /// supervises are not stopped by it
    [Test]
    public async Task A_different_binary_stops_replaces_reconfigures_and_starts() =>
        await Assert.That(InstallPlan.For(serviceExists: true, binaryCurrent: false, running: true))
            .IsEquivalentTo([StopService, PrepareDirectory, CopyBinary, ConfigureService, StartService]);

    [Test]
    public async Task The_same_binary_stopped_is_reconfigured_and_started() =>
        await Assert.That(InstallPlan.For(serviceExists: true, binaryCurrent: true, running: false))
            .IsEquivalentTo([ConfigureService, StartService]);

    [Test]
    public async Task The_same_binary_running_needs_nothing() =>
        await Assert.That(InstallPlan.For(serviceExists: true, binaryCurrent: true, running: true)).IsEmpty();
}
