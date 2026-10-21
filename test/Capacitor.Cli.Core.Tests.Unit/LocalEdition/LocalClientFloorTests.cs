using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalClientFloorTests {
    [Test]
    [Arguments("1.3.1-alpha.0.4", true)]
    [Arguments("1.3.1", true)]
    [Arguments("1.4.0-beta.1", true)]
    [Arguments("2.0.0", true)]
    [Arguments("1.3.0", false)]
    [Arguments("1.3.0+abc123", false)]
    [Arguments("1.3.0-beta.10", false)]
    [Arguments("1.2.2", false)]
    [Arguments("0.12.0", false)]
    [Arguments("unknown", false)]
    [Arguments("garbage", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Admits_every_build_after_the_newest_refused_and_nothing_it_cannot_read(string? version, bool admitted) =>
        await Assert.That(LocalClientFloor.Admits(version)).IsEqualTo(admitted);
}
