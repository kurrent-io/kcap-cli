using System.Runtime.Versioning;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonRunnerVendorModelsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// Startup records baselines for every advertised vendor, so a catalog-only vendor (never
    /// unattended) still has its binary and catalog files fingerprinted for the watcher.
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Startup_fingerprints_binary_and_catalog_paths_for_every_advertised_vendor() {
        Skip.Unless(!OperatingSystem.IsWindows(), "The stub binary is a POSIX shell script.");
        var pi      = Tmp.CreateExecutable("pi", "#!/bin/sh\nexit 0\n");
        var auth    = Tmp.PathTo("auth.json");
        var factory = new StubCatalogFactory("pi", [new("p/a", "A · p")], paths: [auth]) { CliPathOverride = pi };

        var binaries = DaemonRunner.FingerprintUnattendedVendors(TestBinaries.None, [factory], ["pi"]);
        var catalogs = VendorModelCatalogs.FingerprintCatalogPaths([factory], ["pi"]);

        await Assert.That(binaries["pi"]).IsNotNull();
        await Assert.That(catalogs["pi"].Single()).IsEqualTo(new CatalogPathStat(auth, false, 0, 0));
    }
}
