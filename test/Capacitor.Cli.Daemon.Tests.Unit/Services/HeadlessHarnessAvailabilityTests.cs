using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The daemon advertises a headless harness only when that harness's CLI resolves on this host.</summary>
public class HeadlessHarnessAvailabilityTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    HeadlessPromptRunner Runner(BinaryProbe binaries) {
        var harnesses = TestHarnesses.Under(Home, binaries);
        var config    = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };

        return new([
            new ClaudeHeadlessHarness(harnesses, config, TimeProvider.System, NullLogger<ClaudeHeadlessHarness>.Instance),
            new CodexHeadlessHarness(harnesses, config, TimeProvider.System, NullLogger<CodexHeadlessHarness>.Instance)
        ], TimeProvider.System, NullLogger<HeadlessPromptRunner>.Instance, CancellationToken.None);
    }

    [Test]
    public async Task A_harness_is_advertised_only_when_its_binary_resolves() {
        using var claudeOnly = Runner(TestBinaries.Searching(Bin, "claude"));
        using var none       = Runner(TestBinaries.None);

        await Assert.That(claudeOnly.AdvertisedHarnesses()).IsEquivalentTo(["claude"]);
        await Assert.That(none.AdvertisedHarnesses()).IsEmpty();
    }
}
