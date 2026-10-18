using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class PortHolderTests {
    [Test]
    public async Task Parses_the_first_process_of_lsof_field_output() =>
        await Assert.That(PortHolder.Parse("p812\ncnginx\nf6\np813\ncnginx\n")).IsEqualTo(new PortHolder(812, "nginx"));

    [Test]
    public async Task Nothing_listening_is_null() =>
        await Assert.That(PortHolder.Parse("")).IsNull();
}
