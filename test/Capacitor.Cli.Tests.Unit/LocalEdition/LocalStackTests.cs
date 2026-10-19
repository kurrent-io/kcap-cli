using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows)]
public class LocalStackTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _log = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(null, Home);

    FakeLocalServerService Service => field ??= new(_log, Home);

    LocalStack Stack => field ??= new(Root, Home, Service, new HttpClient(), new FakeTimeProvider(), new FakeOutgoingRecorders(_log), new ConfigRoot(Tmp.PathTo("cfg")));

    static readonly LocalInstallation Active = new("abc123", 9473, "http://localhost:9473", "1.2.3");

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    void PublishActive() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root, InstallRegistry.Empty.WithActive(Active));
    }

    LifecycleJournalEntry BeginPendingSetup() {
        using var lease = LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();

        var details = new SetupDetails("0123456789abcdef0123456789abcdef",9474, "1.2.3", "http://localhost:9474", Tmp.PathTo("cfg"), "default");

        return lease.Begin(LocalSetupJournal.Operation, LocalSetupJournal.Pending, ServerStart.Fenced, details.InstallationId,
            LocalSetupJournal.Build(details));
    }

    [Test]
    public async Task Read_with_no_registry_is_not_installed() =>
        await Assert.That(Stack.Read()).IsEqualTo(LocalStackState.NotInstalled.Instance);

    [Test]
    public async Task Read_of_an_unreadable_registry_names_it() {
        Root.EnsureCreated();
        File.WriteAllText(Root.RegistryPath, "{not json");

        var state = Stack.Read();

        await Assert.That(state).IsTypeOf<LocalStackState.Unreadable>();
        await Assert.That(((LocalStackState.Unreadable)state).Path).IsEqualTo(Root.RegistryPath);
    }

    [Test]
    public async Task Read_reports_an_interrupted_setup_instead_of_the_registry() {
        PublishActive();
        BeginPendingSetup();

        await Assert.That(Stack.Read()).IsEqualTo(new LocalStackState.Interrupted(LocalSetupJournal.Operation, LocalSetupJournal.Pending));
    }

    [Test]
    public async Task Read_of_an_installation_whose_unit_serves_this_root_is_ours() {
        PublishActive();
        Service.Install(LocalServerUnit.ForInstallation(Root, Active.Port));

        var state = (LocalStackState.Installed)Stack.Read();

        await Assert.That(state.Installation).IsEqualTo(Active);
        await Assert.That(state.Unit.Port).IsEqualTo(Active.Port);
        await Assert.That(state.UnitAbsent).IsFalse();
        await Assert.That(state.Owner).IsEqualTo(OwnerDecision.Ours);
    }

    /// <summary>Telling another existing root from this one by another spelling takes a probe written into the root,
    /// which only the lease may do.</summary>
    [Test]
    public async Task Read_cannot_tell_an_existing_other_root_apart_and_writes_nothing() {
        PublishActive();
        Service.OwnerOverride = new(new OwnerReading.Root(Tmp.CreateDir("other")), OwnerReading.Absent.Instance);
        var before = Directory.GetFileSystemEntries(Root.Directory);

        var state = (LocalStackState.Installed)Stack.Read();

        await Assert.That(state.UnitAbsent).IsFalse();
        await Assert.That(state.Owner).IsEqualTo(OwnerDecision.Unknown);
        await Assert.That(Directory.GetFileSystemEntries(Root.Directory)).IsEquivalentTo(before);
    }

    [Test]
    public async Task RunAsync_probes_an_existing_other_root_and_finds_it_not_ours() {
        PublishActive();
        Service.OwnerOverride = new(new OwnerReading.Root(Tmp.CreateDir("other")), OwnerReading.Absent.Instance);

        var seen = await Stack.RunAsync(new RecordingProgress<LocalInstallEvent>(), (_, state, _) => Task.FromResult(state), CancellationToken.None);

        await Assert.That(((LocalStackState.Installed)seen).Owner).IsEqualTo(OwnerDecision.NotOurs);
    }

    [Test]
    public async Task Read_of_an_installation_with_no_unit_says_so() {
        PublishActive();

        var state = (LocalStackState.Installed)Stack.Read();

        await Assert.That(state.UnitAbsent).IsTrue();
    }

    [Test]
    public async Task RunAsync_undoes_an_interrupted_setup_before_the_command_reads_the_state() {
        BeginPendingSetup();
        var progress = new RecordingProgress<LocalInstallEvent>();

        var seen = await Stack.RunAsync(progress, (_, state, _) => Task.FromResult(state), CancellationToken.None);

        await Assert.That(seen).IsEqualTo(LocalStackState.NotInstalled.Instance);
        await Assert.That(File.Exists(Root.JournalPath)).IsFalse();
        await Assert.That(progress.Reports.OfType<LocalInstallEvent.Recovered>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_hands_the_command_the_installation() {
        PublishActive();

        var seen = await Stack.RunAsync(new RecordingProgress<LocalInstallEvent>(), (_, state, _) => Task.FromResult(state), CancellationToken.None);

        await Assert.That(((LocalStackState.Installed)seen).Installation).IsEqualTo(Active);
    }

    [Test]
    public async Task RunAsync_does_not_run_the_command_while_another_holds_the_lease() {
        var time = new FakeTimeProvider();
        var stack = new LocalStack(Root, Home, Service, new HttpClient(), time, new FakeOutgoingRecorders(_log), new ConfigRoot(Tmp.PathTo("cfg")));
        using var held = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        var ran = false;

        var run = stack.RunAsync(new RecordingProgress<LocalInstallEvent>(), (_, _, _) => {
            ran = true;
            return Task.FromResult(0);
        }, CancellationToken.None);

        while (!run.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        await Assert.ThrowsAsync<LocalLifecycleException>(() => run);
        await Assert.That(ran).IsFalse();
    }
}
