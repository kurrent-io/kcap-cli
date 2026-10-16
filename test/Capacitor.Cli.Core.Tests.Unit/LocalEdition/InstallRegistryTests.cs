using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class InstallRegistryTests {
    static readonly LocalInstallation Installation = new("inst-1", 51234, "http://localhost:51234", "1.4.0");

    [Test]
    public async Task Retiring_moves_the_active_installation_to_the_retired_list() {
        var registry = InstallRegistry.Empty.WithActive(Installation).RetireActive();

        await Assert.That(registry.Active).IsNull();
        await Assert.That(registry.Retired.Single()).IsEqualTo(new RetiredInstallation("inst-1", 51234, "http://localhost:51234"));
    }

    [Test]
    public async Task A_new_installation_never_takes_a_retired_port() {
        var registry = InstallRegistry.Empty.WithActive(Installation).RetireActive();

        await Assert.That(() => registry.WithActive(Installation with { Id = "inst-2" })).Throws<ArgumentException>();
    }
}
