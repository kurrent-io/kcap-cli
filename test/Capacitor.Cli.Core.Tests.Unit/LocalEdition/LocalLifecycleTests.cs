using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalLifecycleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    static readonly string[] Phases = ["staged", "installed", "started"];

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    [Test]
    public async Task Two_setups_racing_produce_one_installation() {
        var lifecycle = new LocalLifecycle(Root, [], TimeProvider.System);

        var results = await Task.WhenAll(
            lifecycle.RunAsync(Wait, EnsureInstalled, CancellationToken.None),
            lifecycle.RunAsync(Wait, EnsureInstalled, CancellationToken.None));

        await Assert.That(results[1]).IsEqualTo(results[0]);
        var registry = (StateRead<InstallRegistry>.Present)LocalStateFiles.ReadRegistry(Root);
        await Assert.That(registry.Value.Active).IsEqualTo(results[0]);
    }

    [Test]
    [MethodDataSource(nameof(CrashPoints))]
    public async Task A_crash_at_any_phase_is_recovered_by_the_next_command(string crashAt) {
        var recovery = new UndoSetup();
        var lifecycle = new LocalLifecycle(Root, [recovery], TimeProvider.System);

        await Assert.That(() => lifecycle.RunAsync(Wait, (lease, _) => SetupCrashingAt(lease, crashAt), CancellationToken.None))
            .Throws<SimulatedCrash>();

        var ran = await lifecycle.RunAsync(Wait, (_, _) => Task.FromResult(true), CancellationToken.None);

        await Assert.That(ran).IsTrue();
        await Assert.That(recovery.Recovered!.Phase).IsEqualTo(crashAt);
        await Assert.That(recovery.Recovered.Details["port"]).IsEqualTo("51234");
        await Assert.That(LocalStateFiles.ReadJournal(Root)).IsTypeOf<StateRead<LifecycleJournalEntry>.Absent>();
        await Assert.That(LocalStateFiles.ReadRegistry(Root)).IsTypeOf<StateRead<InstallRegistry>.Absent>();
    }

    public static IEnumerable<Func<string>> CrashPoints() => Phases.Select(p => (Func<string>)(() => p));

    /// <summary>Undoing an operation this build has never heard of could delete what a newer kcap meant to
    /// keep, so it is refused and the journal left exactly as it was.</summary>
    [Test]
    public async Task An_operation_this_build_cannot_recover_is_left_untouched() {
        var lifecycle = new LocalLifecycle(Root, [new UndoSetup()], TimeProvider.System);
        await lifecycle.RunAsync(Wait, (lease, _) => Task.FromResult(lease.Begin("migrate-v9", "halfway", ServerStart.Deny)), CancellationToken.None);
        var before = File.ReadAllText(Root.JournalPath);
        var ran    = false;

        await Assert.That(() => lifecycle.RunAsync(Wait, (_, _) => Task.FromResult(ran = true), CancellationToken.None))
            .Throws<LocalLifecycleException>();

        await Assert.That(ran).IsFalse();
        await Assert.That(File.ReadAllText(Root.JournalPath)).IsEqualTo(before);
    }

    [Test]
    public async Task An_unreadable_journal_stops_every_command() {
        Tmp.CreateFile(["local", "journal.json"], "{ truncated");
        var lifecycle = new LocalLifecycle(Root, [new UndoSetup()], TimeProvider.System);

        await Assert.That(() => lifecycle.RunAsync(Wait, (_, _) => Task.FromResult(true), CancellationToken.None))
            .Throws<LocalLifecycleException>();
    }

    [Test]
    public async Task A_recovery_that_leaves_the_journal_behind_is_an_error() {
        var lifecycle = new LocalLifecycle(Root, [new ForgetfulRecovery()], TimeProvider.System);
        await lifecycle.RunAsync(Wait, (lease, _) => Task.FromResult(lease.Begin("setup", "staged", ServerStart.Deny)), CancellationToken.None);

        await Assert.That(() => lifecycle.RunAsync(Wait, (_, _) => Task.FromResult(true), CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    static async Task<LocalInstallation> EnsureInstalled(LifecycleLease lease, CancellationToken ct) {
        if (lease.ReadRegistry() is StateRead<InstallRegistry>.Present { Value.Active: { } existing }) return existing;

        // Long enough that an unserialized second caller would read the registry before this publishes.
        await Task.Delay(100, ct);

        var installation = new LocalInstallation(Guid.NewGuid().ToString("N"), 51234, "http://localhost:51234", "1.4.0");
        lease.PublishRegistry(InstallRegistry.Empty.WithActive(installation));

        return installation;
    }

    static Task<bool> SetupCrashingAt(LifecycleLease lease, string crashAt) {
        var entry = lease.Begin("setup", Phases[0], ServerStart.Deny, "inst-1", new Dictionary<string, string> { ["port"] = "51234" });

        foreach (var phase in Phases) {
            if (phase != entry.Phase) entry = lease.Advance(entry, phase, phase == "started" ? ServerStart.Allow : ServerStart.Deny);
            if (phase == crashAt) throw new SimulatedCrash();
        }

        lease.Complete();

        return Task.FromResult(true);
    }

    sealed class SimulatedCrash : Exception;

    sealed class UndoSetup : ILifecycleRecovery {
        public LifecycleJournalEntry? Recovered { get; private set; }

        public string Operation => "setup";

        public Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
            Recovered = interrupted;
            lease.Complete();

            return Task.CompletedTask;
        }
    }

    sealed class ForgetfulRecovery : ILifecycleRecovery {
        public string Operation => "setup";

        public Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) => Task.CompletedTask;
    }
}
