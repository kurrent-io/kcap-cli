using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests for <see cref="MachineId"/> — the AI-1207 stable per-machine id the daemon reports at
/// registration (distinct from <see cref="MachineIdProvider"/>'s AI-1134 memory-tagging id; see
/// MachineIdTests.cs for that one).
///
/// PathHelpers.ConfigDir is static readonly — captured once at class-load time from
/// KCAP_CONFIG_DIR. RepoPathStoreGlobalSetup.[Before(Assembly)] sets that env var to a shared
/// temp dir before PathHelpers is first touched, so all path-based tests in this process share
/// that same base dir. [NotInParallel] plus per-test cleanup of machine.json keeps these tests
/// from racing each other or RepoPathStoreTests/TokenStoreProfileTests over shared files.
/// </summary>
[NotInParallel(nameof(MachineIdFileTests))]
public class MachineIdFileTests {
    static string MachinePath => Path.Combine(RepoPathStoreGlobalSetup.SharedConfigDir, "machine.json");

    [Before(Test)]
    public void DeleteMachineJson() {
        if (File.Exists(MachinePath)) File.Delete(MachinePath);
    }

    [Test]
    public async Task Get_ReturnsNonEmptyId() {
        var id = MachineId.Get();

        await Assert.That(id).IsNotNull();
        await Assert.That(id).IsNotEmpty();
    }

    [Test]
    public async Task Get_IsStableAcrossCalls() {
        var first  = MachineId.Get();
        var second = MachineId.Get();

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Get_PersistsSoAFreshReadReturnsTheSameId() {
        var id = MachineId.Get();

        // Simulates a new process: reads machine.json straight off disk rather than relying on
        // whatever Get() might keep in memory.
        var persisted = MachineId.ReadPersisted();

        await Assert.That(persisted).IsEqualTo(id);
    }

    [Test]
    public async Task Get_WhenMachineJsonAlreadyExists_ReturnsThePersistedValueRatherThanRegenerating() {
        // Simulate a peer process having already won the first-write race before we ever call Get().
        var seeded = MachineId.Get();
        var before = File.ReadAllText(MachinePath);

        var again = MachineId.Get();

        await Assert.That(again).IsEqualTo(seeded);
        await Assert.That(File.ReadAllText(MachinePath)).IsEqualTo(before);
    }
}
