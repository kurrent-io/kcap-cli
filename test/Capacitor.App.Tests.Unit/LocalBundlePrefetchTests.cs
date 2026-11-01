using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class LocalBundlePrefetchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static SetupFunnel Funnel() => CliTelemetry.Disabled(TimeProvider.System).Funnel;

    /// A fetch that prints what it is given and runs until interrupted, recording that it was.
    sealed class HeldFetch {
        public readonly TaskCompletionSource Interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<StreamedLine>? Print;

        public FakeKcapCli Cli() => new() {
            LocalFetchBehavior = async (onLine, interrupt) => {
                Print = onLine;
                Spawned.TrySetResult();
                await using (interrupt.Register(() => Interrupted.TrySetResult())) {
                    await Task.WhenAny(Interrupted.Task, Release.Task);
                }
                return new StreamingResult(interrupt.IsCancellationRequested ? 130 : 0, false, []);
            },
        };
    }

    [Test]
    public async Task Start_runs_one_fetch_however_often_it_is_called() {
        var cli = new FakeKcapCli();
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        prefetch.Start();
        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);
        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(cli.LocalFetchCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_fetch_that_throws_settles_without_faulting() {
        var cli = new FakeKcapCli { LocalFetchBehavior = (_, _) => throw new InvalidOperationException("no spawn") };
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(prefetch.Completion.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_available_local_edition_with_nothing_installed_is_preselected_and_fetched() {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            return edition.LocalPreselected;
        });
        await fetch.Spawned.Task.WaitAsync(Wait);
        fetch.Release.SetResult();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(preselected).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task Nothing_is_preselected_or_fetched_where_local_cannot_or_need_not_be_set_up(
            bool unavailable, bool locked, bool installed) {
        var cli = new FakeKcapCli();
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(
                Funnel(), () => unavailable ? EditionStepViewModel.UnsupportedPlatform : null, () => locked,
                leadWithLocal: () => !installed, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            return edition.LocalPreselected;
        });

        await Assert.That(preselected).IsFalse();
        await Assert.That(prefetch.Started).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(EditionChoice.Cloud)]
    [Arguments(EditionChoice.ExistingWorkspace)]
    public async Task Choosing_a_cloud_workspace_stops_the_fetch(EditionChoice choice) {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var chosen = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            await fetch.Spawned.Task.WaitAsync(Wait);
            var command = choice == EditionChoice.Cloud ? edition.ChooseCloudCommand : edition.SignInCommand;
            await command.Execute().ToTask();
            return edition.Choice;
        });

        await fetch.Interrupted.Task.WaitAsync(Wait);
        await prefetch.Completion.WaitAsync(Wait);
        await Assert.That(chosen).IsEqualTo(choice);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Choosing_local_leaves_the_fetch_running() {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var running = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            await fetch.Spawned.Task.WaitAsync(Wait);
            await edition.ChooseLocalCommand.Execute().ToTask();
            return prefetch.Running;
        });
        fetch.Release.SetResult();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(running).IsTrue();
        await Assert.That(fetch.Interrupted.Task.IsCompleted).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task The_wizard_leads_with_local_only_before_anything_is_installed(bool installed, bool expected) {
        var root = new LocalRoot(Config.PathTo("local"));
        if (installed) Config.CreateFile("local/install.json", "{}");
        var prefetch = new LocalBundlePrefetch(() => new FakeKcapCli(), CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { LocalRoot = root };
            var graph = WizardComposition.BuildGraph(harness.Options() with { Prefetch = prefetch });
            await graph.ViewModel.PendingEnterForTesting;

            return graph.Steps.OfType<EditionStepViewModel>().Single().LocalPreselected;
        });
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(preselected).IsEqualTo(expected);
        await Assert.That(prefetch.Started).IsEqualTo(expected);
    }

    /// The install reads the bundle the fetch is still downloading rather than download it a second time beside it,
    /// and the page shows that download as its own.
    [Test]
    public async Task An_install_waits_for_a_running_fetch_and_shows_its_download() {
        var fetch = new HeldFetch();
        var prefetchCli = fetch.Cli();
        var prefetch = new LocalBundlePrefetch(() => prefetchCli, CancellationToken.None);
        var setup = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };
        var controller = new LocalInstallController(
            () => setup, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel, prefetch);
        var settled = new TaskCompletionSource<LocalInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Settled += outcome => settled.TrySetResult(outcome);

        prefetch.Start();
        await fetch.Spawned.Task.WaitAsync(Wait);
        await controller.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        fetch.Print!(new StreamedLine(ProcessStreamKind.Stdout, """{"v":1,"event":"downloading","bytes":100}"""));
        fetch.Print!(new StreamedLine(ProcessStreamKind.Stdout, """{"v":1,"event":"download_progress","received":40}"""));
        var during = (setup.SetupLocalCallCount, controller.Progress.Download.State, controller.Progress.DownloadFraction);

        fetch.Release.SetResult();
        var outcome = await settled.Task.WaitAsync(Wait);

        await Assert.That(during).IsEqualTo((0, RowState.Running, (double?)0.4));
        await Assert.That(setup.SetupLocalCallCount).IsEqualTo(1);
        await Assert.That(outcome).IsTypeOf<LocalInstallOutcome.Installed>();
    }

    [Test]
    public async Task Cancelling_an_install_that_waits_on_the_fetch_stops_both_without_spawning_setup() {
        var fetch = new HeldFetch();
        var prefetchCli = fetch.Cli();
        var prefetch = new LocalBundlePrefetch(() => prefetchCli, CancellationToken.None);
        var setup = new FakeKcapCli();
        var controller = new LocalInstallController(
            () => setup, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel, prefetch);

        prefetch.Start();
        await fetch.Spawned.Task.WaitAsync(Wait);
        await controller.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        await controller.SettleAsync().WaitAsync(Wait);

        await Assert.That(setup.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(fetch.Interrupted.Task.IsCompleted).IsTrue();
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }
}
