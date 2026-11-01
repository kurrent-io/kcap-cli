using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class LocalMachineTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Reads_the_id_the_hooks_and_daemon_share() {
        var id = new MachineId(Config.Root).Get();

        await Assert.That(new LocalMachine(new MachineId(Config.Root)).Id).IsEqualTo(id);
    }

    /// The app never mints the id: one it created would name a machine nothing was recorded on.
    [Test]
    public async Task Creates_nothing_and_picks_up_an_id_written_later() {
        var machine = new LocalMachine(new MachineId(Config.Root));

        await Assert.That(machine.Id).IsNull();
        await Assert.That(new MachineId(Config.Root).IsPersisted).IsFalse();

        var id = new MachineId(Config.Root).Get();

        await Assert.That(machine.Id).IsEqualTo(id);
    }
}
