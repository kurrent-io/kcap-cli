using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

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

    [Test]
    [Arguments("kcap-server", true)]
    [Arguments("kcap-server.exe", true)]
    [Arguments("KCAP-SERVER", true)]
    [Arguments("python3", false)]
    [Arguments("kcap-server-old", false)]
    public async Task Only_the_servers_own_name_reads_as_the_server(string command, bool server) =>
        await Assert.That(new PortHolder(1, command).IsServerProcess).IsEqualTo(server);

    /// <summary>Windows names the holder from its TCP table, not from <c>lsof</c>: a listener this test opens on
    /// loopback is found as this process.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task Windows_names_the_process_listening_on_loopback() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var holder = PortHolder.Find(port);

            await Assert.That(holder).IsNotNull();
            await Assert.That(holder!.Pid).IsEqualTo(Environment.ProcessId);
        } finally {
            listener.Stop();
        }
    }
}
