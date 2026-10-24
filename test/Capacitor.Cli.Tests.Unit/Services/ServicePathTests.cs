using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServicePathTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    static readonly char Sep = Path.PathSeparator;

    [Test]
    public async Task A_harness_only_the_current_path_finds_yields_the_current_path_then_the_units_own_directories() {
        Skip.When(OperatingSystem.IsWindows(), "the stub is a POSIX script");

        var baked = Tmp.CreateDir("baked").Path;
        var both  = Tmp.CreateDir("both").Path;
        var fresh = Tmp.CreateDir("fresh").Path;
        Tmp.CreateExecutable("fresh/claude", "#!/bin/sh\n");

        var refreshed = ServicePath.Refreshed($"{baked}{Sep}{both}", $"{fresh}{Sep}{both}", HarnessRegistry.FromEnvironment(Home));

        await Assert.That(refreshed).IsEqualTo($"{fresh}{Sep}{both}{Sep}{baked}");
    }

    [Test]
    public async Task A_harness_the_unit_already_finds_needs_no_rewrite() {
        Skip.When(OperatingSystem.IsWindows(), "the stub is a POSIX script");

        var shared = Tmp.CreateDir("shared").Path;
        var other  = Tmp.CreateDir("other").Path;
        Tmp.CreateExecutable("shared/claude", "#!/bin/sh\n");

        await Assert.That(ServicePath.Refreshed(shared, $"{other}{Sep}{shared}", HarnessRegistry.FromEnvironment(Home))).IsNull();
    }

    [Test]
    public async Task No_current_path_means_no_rewrite() {
        await Assert.That(ServicePath.Refreshed("/usr/bin", null, HarnessRegistry.FromEnvironment(Home))).IsNull();
    }

    /// <summary>Detection counts the Kiro IDE launcher, which no daemon can host.</summary>
    [Test]
    public async Task An_ide_launcher_alone_needs_no_rewrite() {
        Skip.When(OperatingSystem.IsWindows(), "the stub is a POSIX script");

        var baked = Tmp.CreateDir("baked").Path;
        var fresh = Tmp.CreateDir("fresh").Path;
        Tmp.CreateExecutable("fresh/kiro", "#!/bin/sh\n");

        await Assert.That(ServicePath.Refreshed(baked, $"{fresh}{Sep}{baked}", HarnessRegistry.FromEnvironment(Home))).IsNull();
    }
}
