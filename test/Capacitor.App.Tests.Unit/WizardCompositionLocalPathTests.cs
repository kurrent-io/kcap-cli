using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup;
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
            await graph.ViewModel.NextCommand.Execute().ToTask();
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

    /// The terminal command is part of the install itself, before any harness is chosen; otherwise
    /// the app offers it on first launch after setup has finished.
    [Test]
    public async Task A_settled_local_install_puts_kcap_on_the_terminal_path() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(NotInstalled());
        var installer = new RecordingInstaller();
        harness.ShimInstaller = installer;
        var exit = new TaskCompletionSource<StreamingResult>();
        Action<StreamedLine>? print = null;
        harness.Cli.SetupLocalBehavior = (onLine, _, _) => {
            print = onLine;
            return exit.Task;
        };

        var (during, targets) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseLocalCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;
            await graph.ViewModel.NextCommand.Execute().ToTask();
            var during = installer.Targets.Count;

            print!(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            exit.SetResult(new StreamingResult(0, false, []));
            await WizardFixtures.WaitUntilAsync(() => installer.Targets.Count == 1, what: "the settled install to link kcap");

            return (during, installer.Targets.ToList());
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(during).IsEqualTo(0);
        await Assert.That(targets.Single()).IsEqualTo(harness.ShimTarget);
    }

    sealed class RecordingInstaller : ICliPathInstaller {
        public readonly List<string> Targets = [];
        public string Disclosure => "";
        public ShimPreflight Preflight(string target) => ShimPreflight.Installable;
        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
            Targets.Add(target);
            return Task.FromResult(new ShimResult(ShimOutcome.Installed, null));
        }
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

        var (start, eyebrow, backVisible, afterBack, signInShown, installs, setups) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.ViewModel.PendingEnterForTesting;
            var start = graph.ViewModel.Current.Id;
            var eyebrow = graph.ViewModel.Eyebrow;
            var backVisible = graph.ViewModel.BackVisible;

            await graph.ViewModel.BackCommand.Execute().ToTask();
            await WizardFixtures.WaitUntilAsync(
                () => harness.Lane.Requests.Count == 1, what: "composition to enable the daemon");

            return (start, eyebrow, backVisible, graph.ViewModel.Current.Id,
                !graph.Steps.OfType<SignInStepViewModel>().Single().PassThrough,
                harness.Lane.Requests.Count(r => r.Verb == MutationVerb.Install), harness.Cli.SetupLocalCallCount);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(start).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(eyebrow).StartsWith("STEP 1 OF 4 ·");
        await Assert.That(backVisible).IsFalse();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(signInShown).IsFalse();
        await Assert.That(installs).IsEqualTo(1);
        await Assert.That(setups).IsEqualTo(0);
    }

    /// A web sign-up opened before the local install began does not pull the run onto sign-in.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pending_setup_starts_at_install(bool webSignUpOpened) {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var exit = new TaskCompletionSource<StreamingResult>();
        harness.Cli.SetupLocalBehavior = (_, _, _) => exit.Task;

        var (current, backVisible, setups, requests) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { ResumeWebSignUp = webSignUpOpened });
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
    /// durable state before Back is restated, not after. Nothing is connected, so Finish later stays hidden.
    [Test]
    public async Task A_pending_setup_that_rolls_back_and_fails_unlocked_offers_back_to_edition() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var root = LocalSetupScript.PendingSetup(Config);
        harness.LocalRoot = root;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Cli.SetupLocalBehavior = async (onLine, _, _) => {
            await release.Task;
            File.Delete(root.JournalPath);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("download_failed")));

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
        await Assert.That(canFinishLater).IsFalse();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Edition);
    }

    /// A workspace signed in at start is this run's: setup opens on harnesses as its first step, with no way back
    /// to a finished sign-in or an edition it cannot change.
    [Test]
    public async Task Cloud_forced_setup_starts_at_harnesses_as_step_one() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var (start, eyebrow, backVisible, canFinishLater, afterBack, install) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = true });
            await graph.ViewModel.PendingEnterForTesting;
            var start = graph.ViewModel.Current.Id;
            var eyebrow = graph.ViewModel.Eyebrow;
            var backVisible = graph.ViewModel.BackVisible;
            var canFinishLater = await graph.ViewModel.FinishLaterCommand.CanExecute.FirstAsync();

            await graph.ViewModel.BackCommand.Execute().ToTask();

            return (start, eyebrow, backVisible, canFinishLater, graph.ViewModel.Current.Id, graph.Steps.OfType<InstallStepViewModel>().Single());
        });

        await Assert.That(start).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(eyebrow).StartsWith("STEP 1 OF 3 ·");
        await Assert.That(backVisible).IsFalse();
        await Assert.That(canFinishLater).IsTrue();
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Harnesses);
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

    /// Before a server is connected the app has nothing to show, so Finish later stays hidden on every page up to
    /// the install, including the first page after stepping back to it.
    [Test]
    public async Task Finish_later_waits_for_a_connected_server_on_every_page() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var (atLaunch, onInstall, backOnEdition) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var atLaunch = graph.ViewModel.FinishLaterVisible;

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseLocalCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;
            var onInstall = (graph.ViewModel.Current.Id, graph.ViewModel.FinishLaterVisible);

            await graph.ViewModel.BackCommand.Execute().ToTask();
            return (atLaunch, onInstall, (graph.ViewModel.Current.Id, graph.ViewModel.FinishLaterVisible));
        });

        await Assert.That(atLaunch).IsFalse();
        await Assert.That(onInstall).IsEqualTo((WizardStepId.Install, false));
        await Assert.That(backOnEdition).IsEqualTo((WizardStepId.Edition, false));
    }

    [Test]
    public async Task Finish_later_appears_once_the_install_connects() {
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
            await graph.ViewModel.NextCommand.Execute().ToTask();
            var during = await finishLater.CanExecute.FirstAsync();

            print!(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            exit.SetResult(new StreamingResult(0, false, []));
            await graph.Install.SettleAsync();
            await graph.ViewModel.PendingAdvanceForTesting;
            // Outcome and Navigating flip on the pool thread that finishes setup. CanExecute is
            // delivered to the UI thread afterwards, so a direct read can still see the disabled value.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // ReactiveCommand publishes CanExecute on its scheduler after the install settles.
            // Wait for that notification rather than reading the cached busy value.
            return (before, during, await finishLater.CanExecute.FirstAsync(enabled => enabled));
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(before).IsFalse();
        await Assert.That(during).IsFalse();
        await Assert.That(after).IsTrue();
    }
}
