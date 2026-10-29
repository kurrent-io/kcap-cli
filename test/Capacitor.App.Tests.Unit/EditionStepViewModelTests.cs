using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class EditionStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    static string Edition(TelemetryEvent e) => e.Properties["edition"]!.GetValue<string>();

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(EditionChoice.Cloud, "cloud")]
    [Arguments(EditionChoice.ExistingWorkspace, "existing_workspace")]
    public async Task Cloud_and_existing_workspace_continue_to_sign_in_and_name_their_branch(EditionChoice choice, string wire) {
        var probe = Probe();

        var current = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, () => null);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn), new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;

            var command = choice == EditionChoice.Cloud ? edition.ChooseCloudCommand : edition.SignInCommand;
            await command.Execute().ToTask();
            await vm.PendingAdvanceForTesting;

            return vm.Current.Id;
        });

        await Assert.That(current).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(probe.Names).IsEquivalentTo(
            ["cli_setup_edition_offered", "cli_setup_edition_chosen"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Edition(probe.Only("cli_setup_edition_chosen"))).IsEqualTo(wire);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_advances_to_install() {
        var probe = Probe();

        var current = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, () => null);
            var vm = new OnboardingViewModel([
                edition, new FakeWizardStep(WizardStepId.Install), new FakeWizardStep(WizardStepId.SignIn),
            ]);
            await vm.PendingEnterForTesting;

            await edition.ChooseLocalCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;

            return vm.Current.Id;
        });

        await Assert.That(current).IsEqualTo(WizardStepId.Install);
        await Assert.That(Edition(probe.Only("cli_setup_edition_chosen"))).IsEqualTo("local");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_is_unavailable_off_macos() {
        var (reason, canChoose) = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { LocalEditionSupported = false };
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();

            return (edition.LocalUnavailableReason, await edition.ChooseLocalCommand.CanExecute.FirstAsync());
        });

        await Assert.That(reason).IsEqualTo("The local edition isn't available on this platform yet.");
        await Assert.That(canChoose).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_inactive_cloud_profile_makes_local_unavailable() {
        WizardCompositionFixtures.WriteConfig(Config.Root, new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new() {
                ["default"] = new Profile(),
                ["work"]    = new Profile { ServerUrl = "https://acme.kcap.ai" },
            },
        });

        var (reason, canChoose) = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();

            return (edition.LocalUnavailableReason, await edition.ChooseLocalCommand.CanExecute.FirstAsync());
        });

        await Assert.That(reason).IsEqualTo(
            "This machine is set up for a cloud workspace. Switching to local setup isn't supported yet.");
        await Assert.That(canChoose).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Coming_back_offers_once_and_records_each_choice() {
        var probe = Probe();

        var current = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, () => null);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn)]);
            await vm.PendingEnterForTesting;

            await edition.ChooseCloudCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;
            await vm.BackCommand.Execute().ToTask();
            await edition.SignInCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;

            return vm.Current.Id;
        });

        await Assert.That(current).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(probe.Captured("cli_setup_edition_offered").Count).IsEqualTo(1);
        await Assert.That(probe.Captured("cli_setup_edition_chosen").Select(Edition).ToList())
            .IsEquivalentTo(["cloud", "existing_workspace"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_wizard_opens_on_the_edition_choice() {
        var current = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            return graph.ViewModel.Current;
        });

        await Assert.That(current).IsTypeOf<EditionStepViewModel>();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("CloudEditionButton")]
    [Arguments("ExistingWorkspaceButton")]
    public async Task The_pane_shows_the_cards_in_place_of_next_and_each_cloud_action_reaches_sign_in(string action) {
        var (localShown, cloudShown, signInShown, nextShown, after) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn)]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            T? Named<T>(string name) where T : Control =>
                window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

            var local = Named<Button>("LocalEditionButton")?.IsEffectivelyVisible;
            var cloud = Named<Button>("CloudEditionButton")!;
            var cloudVisible = cloud.IsEffectivelyVisible;
            var signIn = Named<Button>("ExistingWorkspaceButton")?.IsEffectivelyVisible;
            var next = Named<Button>("NextButton")?.IsEffectivelyVisible;

            Named<Button>(action)!.Command!.Execute(null);
            await vm.PendingAdvanceForTesting;
            Dispatcher.UIThread.RunJobs();
            var current = vm.Current.Id;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (local, cloudVisible, signIn, next, current);
        });

        await Assert.That(localShown).IsTrue();
        await Assert.That(cloudShown).IsTrue();
        await Assert.That(signInShown).IsTrue();
        await Assert.That(nextShown).IsFalse();
        await Assert.That(after).IsEqualTo(WizardStepId.SignIn);
    }

    /// The footer sits inside a disabled button, whose template may fade everything it holds;
    /// the reason is the one part of the card that must stay at full strength.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unavailable_local_card_says_why_in_its_own_footer() {
        var (inCard, footerShown, enabled, help, footerOpacity, headOpacity) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(
                CliTelemetry.Disabled(TimeProvider.System).Funnel, () => EditionStepViewModel.UnsupportedPlatform);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn)]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var local = window.GetVisualDescendants().OfType<Button>().First(c => c.Name == "LocalEditionButton");
            var reason = window.GetVisualDescendants().OfType<TextBlock>().First(c => c.Name == "LocalUnavailableText");
            var head = local.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("onboardingEditionHead"));
            var result = (reason.GetVisualAncestors().Contains(local), reason.IsEffectivelyVisible, local.IsEffectivelyEnabled,
                Avalonia.Automation.AutomationProperties.GetHelpText(local), EffectiveOpacity(reason), EffectiveOpacity(head));

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return result;
        });

        await Assert.That(inCard).IsTrue();
        await Assert.That(footerShown).IsTrue();
        await Assert.That(enabled).IsFalse();
        await Assert.That(help).IsEqualTo(EditionStepViewModel.UnsupportedPlatform);
        await Assert.That(footerOpacity).IsEqualTo(1.0);
        await Assert.That(headOpacity).IsEqualTo(0.5);

        static double EffectiveOpacity(Avalonia.Visual visual) =>
            visual.GetSelfAndVisualAncestors().Aggregate(1.0, (opacity, v) => opacity * v.Opacity);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_notice_stays_until_the_next_choice() {
        var (shown, afterChoice) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, () => null);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn)]);
            await vm.PendingEnterForTesting;

            edition.ShowNotice("The wait ran out.");
            var shown = (edition.Notice, edition.NoticeVisible);
            await edition.ChooseCloudCommand.Execute().ToTask();

            return (shown, (edition.Notice, edition.NoticeVisible));
        });

        await Assert.That(shown).IsEqualTo(("The wait ran out.", true));
        await Assert.That(afterChoice).IsEqualTo(((string?)null, false));
    }
}
