using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class PortHolderTests {
    [Test]
    [Arguments("127.0.0.1:9473")]
    [Arguments("*:9473")]
    [Arguments("[::1]:9473")]
    [Arguments("[::]:9473")]
    public async Task A_listener_on_loopback_or_every_address_holds_the_port(string address) =>
        await Assert.That(PortHolder.Parse($"p812\ncnginx\nf6\nn{address}\n")).IsEqualTo(new PortHolder(812, "nginx"));

    [Test]
    public async Task A_listener_on_another_address_only_shares_the_port() =>
        await Assert.That(PortHolder.Parse("p812\ncnginx\nf6\nn192.168.1.5:9473\n")).IsNull();

    [Test]
    public async Task The_first_conflicting_process_is_named() =>
        await Assert.That(PortHolder.Parse("p812\ncnginx\nf6\nn192.168.1.5:9473\np900\ncPython\nf3\nn127.0.0.1:9473\n"))
            .IsEqualTo(new PortHolder(900, "Python"));

    [Test]
    public async Task Nothing_listening_is_null() =>
        await Assert.That(PortHolder.Parse("")).IsNull();
}
