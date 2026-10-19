using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>The local installation's stack moves with the CLI, so its own version never caps the CLI's update.</summary>
public class UpdateAdvisoryLocalInstallationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Url = "http://localhost:47391";

    static readonly UpdateCommand.UpdateCheckResult Result = new("0.11.0", "0.12.0", Newer: true, FromCache: true);

    [Test]
    public async Task The_local_installation_does_not_cap_the_update() {
        ServerVersionStore.Set(Url, "0.11.0", Config.Root, TimeProvider.System);

        var local = new LocalEndpointClass.Local(new("id", 47391, Url, "0.11.0"));
        var advisory = UpdateAdvisoryResolver.Resolve(Result, "latest", Url, local, Config.Root);

        await Assert.That(advisory.Target).IsEqualTo("0.12.0");
        await Assert.That(advisory.ServerCapped).IsFalse();
    }

    [Test]
    public async Task Any_other_server_still_caps_it() {
        ServerVersionStore.Set(Url, "0.11.0", Config.Root, TimeProvider.System);

        var advisory = UpdateAdvisoryResolver.Resolve(Result, "latest", Url, new LocalEndpointClass.Ordinary(), Config.Root);

        await Assert.That(advisory.ServerCapped).IsTrue();
        await Assert.That(advisory.Newer).IsFalse();
    }
}
