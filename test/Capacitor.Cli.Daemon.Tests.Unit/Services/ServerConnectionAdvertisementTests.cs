using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ServerConnectionAdvertisementTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task DaemonConnect_carries_the_configured_vendor_models() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        config.VendorModels = new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] };
        await using var conn = new ServerConnection(config, UnusedTokenStore.Create(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        var connect = conn.BuildDaemonConnect("mac", [], [], null);

        await Assert.That(ReferenceEquals(connect.VendorModels, config.VendorModels)).IsTrue();
    }

    [Test]
    public async Task DaemonConnect_before_any_probe_carries_null_vendor_models() {
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        await using var conn = new ServerConnection(config, UnusedTokenStore.Create(), NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance, TimeProvider.System);

        await Assert.That(conn.BuildDaemonConnect("mac", [], [], null).VendorModels).IsNull();
    }
}
