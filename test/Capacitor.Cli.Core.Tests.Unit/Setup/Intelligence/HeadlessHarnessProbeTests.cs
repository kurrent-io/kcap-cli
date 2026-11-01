using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>A harness counts only where its CLI resolves, the daemon's own rule for advertising it</summary>
public class HeadlessHarnessProbeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Lists_only_the_harnesses_whose_cli_resolves() {
        var binaries = TestBinaries.Searching(Tmp, "codex");
        var home     = new UserHome(Tmp.CreateDir("home"));

        await Assert.That(HeadlessHarnessProbe.Detect(TestHarnesses.Under(home, binaries), binaries)).IsEquivalentTo(new[] { "codex" });
    }

    [Test]
    public async Task Lists_both_in_ordinal_order() {
        var binaries = TestBinaries.Searching(Tmp, "codex", "claude");
        var home     = new UserHome(Tmp.CreateDir("home"));

        await Assert.That(HeadlessHarnessProbe.Detect(TestHarnesses.Under(home, binaries), binaries))
            .IsEquivalentTo(new[] { "claude", "codex" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Nothing_resolves_nothing_listed() {
        var home = new UserHome(Tmp.CreateDir("home"));

        await Assert.That(HeadlessHarnessProbe.Detect(TestHarnesses.Under(home), TestBinaries.None)).IsEmpty();
    }
}
