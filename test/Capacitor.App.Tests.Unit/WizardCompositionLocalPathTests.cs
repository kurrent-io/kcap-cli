using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// The local path through the REAL composition: Edition's Local choice, the Install step over a
/// scripted `kcap setup --local`, and the daemon enabler that waits for the install to settle.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionLocalPathTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int LocalPort = 5123;

    static ServiceSnapshot NotInstalled() =>
        new("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false);

    static void RegisterInstallation(LocalRoot root) {
        root.EnsureCreated();
        LocalStateFiles.PublishRegistry(root, InstallRegistry.Empty.WithActive(
            new("0123456789abcdef0123456789abcdef", LocalPort, LocalSetupScript.ServerUrl, "1.0.0")));
    }

    [Test]
    public async Task Local_path_skips_sign_in_and_enables_the_daemon_only_after_install() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(NotInstalled());
        var exit = new TaskCompletionSource<StreamingResult>();
        Action<StreamedLine>? print = null;
        harness.Cli.SetupLocalBehavior = (onLine, _, _) => {
            print = onLine;
            return exit.Task;
        };

        var (during, visited, after, requests) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            var visited = new List<WizardStepId>();
            graph.ViewModel.WhenAnyValue(x => x.Current).Subscribe(step => visited.Add(step.Id));
            await graph.ViewModel.PendingEnterForTesting;

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseLocalCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;
            var during = (graph.ViewModel.Current.Id, harness.Lane.Requests.Count, harness.Cli.StatusCallCount);

            print!(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            await Task.Delay(100);
            var afterLine = harness.Lane.Requests.Count;
            exit.SetResult(new StreamingResult(0, false, []));
            await WizardFixtures.WaitUntilAsync(
                () => graph.ViewModel.Current.Id == WizardStepId.Harnesses, what: "the install to advance");
            await WizardFixtures.WaitUntilAsync(
                () => harness.Lane.Requests.Count == 1, what: "the settled install to enable the daemon");

            return ((during.Id, during.Count, during.StatusCallCount, afterLine), visited, graph.ViewModel.Current.Id,
                harness.Lane.Requests.ToList());
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(during.Id).IsEqualTo(WizardStepId.Install);
        await Assert.That(during.Count).IsEqualTo(0);
        await Assert.That(during.StatusCallCount).IsEqualTo(0);
        await Assert.That(during.afterLine).IsEqualTo(0);
        await Assert.That(visited).DoesNotContain(WizardStepId.SignIn);
        await Assert.That(after).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Install);
    }

    /// The durable half of the local-path predicate is read at build, on Edition entry and on a settled
    /// install, never per evaluation: a journal written between those reads is not seen until the next one.
    [Test]
    public async Task The_local_path_reads_durable_state_from_a_snapshot() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.NoSetup(Config);

        var (before, unrefreshed, refreshed) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            var before = signIn.PassThrough;

            LocalSetupScript.PendingSetup(Config);
            var unrefreshed = signIn.PassThrough;

            await graph.Steps.OfType<EditionStepViewModel>().Single().OnEnterAsync(CancellationToken.None);

            return (before, unrefreshed, signIn.PassThrough);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(before).IsFalse();
        await Assert.That(unrefreshed).IsFalse();
        await Assert.That(refreshed).IsTrue();
    }

    [Test]
    public async Task Forced_setup_on_a_committed_local_machine_starts_at_harnesses_with_no_route_to_cloud() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        RegisterInstallation(harness.LocalRoot);
        harness.Identity = ("default", LocalSetupScript.ServerUrl, "daemon-a");
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(NotInstalled());

        var (start, backVisible, afterBack, signInShown, installs, setups) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.ViewModel.PendingEnterForTesting;
            var start = graph.ViewModel.Current.Id;
            var backVisible = graph.ViewModel.BackVisible;

            await graph.ViewModel.BackCommand.Execute().ToTask();
            await WizardFixtures.WaitUntilAsync(
                () => harness.Lane.Requests.Count == 1, what: "composition to enable the daemon");

            return (start, backVisible, graph.ViewModel.Current.Id,
                !graph.Steps.OfType<SignInStepViewModel>().Single().PassThrough,
                harness.Lane.Requests.Count(r => r.Verb == MutationVerb.Install), harness.Cli.SetupLocalCallCount);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(start).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(backVisible).IsFalse();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(signInShown).IsFalse();
        await Assert.That(installs).IsEqualTo(1);
        await Assert.That(setups).IsEqualTo(0);
    }

    [Test]
    public async Task Pending_setup_starts_at_install() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var exit = new TaskCompletionSource<StreamingResult>();
        harness.Cli.SetupLocalBehavior = (_, _, _) => exit.Task;

        var (current, backVisible, setups, requests) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            return (graph.ViewModel.Current.Id, graph.ViewModel.BackVisible, harness.Cli.SetupLocalCallCount,
                harness.Lane.Requests.Count);
        });
        exit.SetResult(new StreamingResult(1, false, []));

        await Assert.That(current).IsEqualTo(WizardStepId.Install);
        await Assert.That(backVisible).IsFalse();
        await Assert.That(setups).IsEqualTo(1);
        await Assert.That(requests).IsEqualTo(0);
    }

    /// The run's own rollback clears the journal before it fails, so the settle must republish the
    /// durable state before Back and Finish later are restated, not after.
    [Test]
    public async Task A_pending_setup_that_rolls_back_and_fails_unlocked_offers_back_to_edition() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var root = LocalSetupScript.PendingSetup(Config);
        harness.LocalRoot = root;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Cli.SetupLocalBehavior = async (onLine, _, _) => {
            await release.Task;
            File.Delete(root.JournalPath);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("start_failed")));

            return new StreamingResult(1, false, []);
        };

        var (lockedBefore, backVisible, canBack, canFinishLater, afterBack) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var lastBackVisible = (bool?)null;
            using var watch = graph.ViewModel.WhenAnyValue(x => x.BackVisible).Subscribe(v => lastBackVisible = v);
            var lockedBefore = lastBackVisible;

            release.SetResult();
            await WizardFixtures.WaitUntilAsync(() => graph.Install.Outcome is not null, what: "the install to settle");
            await graph.Install.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var backVisible    = lastBackVisible;
            var canBack        = await graph.ViewModel.BackCommand.CanExecute.FirstAsync();
            var canFinishLater = await graph.ViewModel.FinishLaterCommand.CanExecute.FirstAsync();
            await graph.ViewModel.BackCommand.Execute().ToTask();

            return (lockedBefore, backVisible, canBack, canFinishLater, graph.ViewModel.Current.Id);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(lockedBefore).IsFalse();
        await Assert.That(backVisible).IsTrue();
        await Assert.That(canBack).IsTrue();
        await Assert.That(canFinishLater).IsTrue();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Edition);
    }

    [Test]
    public async Task Cloud_forced_setup_is_unchanged() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var (start, canFinishLater, afterBack, install) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.ViewModel.PendingEnterForTesting;
            var start = graph.ViewModel.Current.Id;
            var canFinishLater = await graph.ViewModel.FinishLaterCommand.CanExecute.FirstAsync();

            await graph.ViewModel.BackCommand.Execute().ToTask();

            return (start, canFinishLater, graph.ViewModel.Current.Id, graph.Steps.OfType<InstallStepViewModel>().Single());
        });

        await Assert.That(start).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(canFinishLater).IsTrue();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(install.PassThrough).IsTrue();
        await Assert.That(harness.Cli.SetupLocalCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_profile_override_naming_another_profile_is_refused_before_any_server_exists() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = "personal", Profiles = new() { ["personal"] = new Profile() } });
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Identity        = null;
        harness.ProfileOverride = "work";

        var refusal = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseLocalCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;

            return graph.Steps.OfType<InstallStepViewModel>().Single().RefusalMessage;
        });

        await Assert.That(refusal).Contains("\"work\"");
        await Assert.That(refusal).Contains("\"personal\"");
        await Assert.That(harness.Cli.SetupLocalCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Finish_later_is_disabled_until_installed() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var exit = new TaskCompletionSource<StreamingResult>();
        Action<StreamedLine>? print = null;
        harness.Cli.SetupLocalBehavior = (onLine, _, _) => {
            print = onLine;
            return exit.Task;
        };

        var (before, during, after) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var finishLater = graph.ViewModel.FinishLaterCommand;
            var before = await finishLater.CanExecute.FirstAsync();

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseLocalCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;
            var during = await finishLater.CanExecute.FirstAsync();

            print!(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            exit.SetResult(new StreamingResult(0, false, []));
            await WizardFixtures.WaitUntilAsync(
                () => graph.Install.Outcome is LocalInstallOutcome.Installed && !graph.ViewModel.Navigating,
                what: "the install to settle and its advance to land");

            return (before, during, await finishLater.CanExecute.FirstAsync());
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(before).IsTrue();
        await Assert.That(during).IsFalse();
        await Assert.That(after).IsTrue();
    }
}
