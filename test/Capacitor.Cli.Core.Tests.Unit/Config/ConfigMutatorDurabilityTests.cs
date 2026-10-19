using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ConfigMutatorDurabilityTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ProfileConfig Touch(ProfileConfig c) => c with { MachineId = "m-1" };

    [Test]
    public async Task Durable_mutation_syncs_after_rename() {
        if (OperatingSystem.IsWindows()) return;

        var path = AppConfig.GetConfigPath(Config.Root);
        var hits = new List<string>();

        using (FaultPoints.Arm(hits.Add))
            ConfigMutator.MutateStrict(Config.Root, Touch, durable: true, new UserHome(Config.Directory));

        var tail = hits.SkipWhile(h => h != $"rename:{path}").ToArray();

        await Assert.That(string.Join("|", tail)).IsEqualTo($"rename:{path}|renamed:{path}|sync:{Config.Directory}");
        await Assert.That(hits).Contains($"flush:{path}");
    }

    [Test]
    public async Task Durable_mutation_into_a_new_multi_level_dir_syncs_each_parent() {
        if (OperatingSystem.IsWindows()) return;

        var x    = Path.Combine(Config.Directory, "x");
        var y    = Path.Combine(x, "y");
        var root = new ConfigRoot(Path.Combine(y, "z"));
        var path = AppConfig.GetConfigPath(root);
        var hits = new List<string>();

        using (FaultPoints.Arm(hits.Add))
            ConfigMutator.MutateStrict(root, Touch, durable: true, new UserHome(Config.Directory));

        var rename = hits.IndexOf($"rename:{path}");

        await Assert.That(string.Join("|", hits.Take(rename).Where(h => h.StartsWith("sync:", StringComparison.Ordinal))))
            .IsEqualTo($"sync:{Config.Directory}|sync:{x}|sync:{y}");
    }

    [Test]
    public async Task A_failed_sync_propagates() {
        if (OperatingSystem.IsWindows()) return;

        using var _ = FaultPoints.Arm(p => {
            if (p == $"sync:{Config.Directory}") throw new IOException("sync failed");
        });

        await Assert.That(() => ConfigMutator.MutateStrict(Config.Root, Touch, durable: true, new UserHome(Config.Directory)))
            .Throws<IOException>();
    }
}
