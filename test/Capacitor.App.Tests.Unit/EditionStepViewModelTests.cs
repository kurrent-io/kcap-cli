using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
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
            var edition = new EditionStepViewModel(probe.Funnel, localOffered: true);
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
    public async Task Local_stops_at_its_hand_off_and_next_cannot_carry_it_into_sign_in() {
        var probe = Probe();

        var (pending, afterChoice, afterNext) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, localOffered: true);
            var vm = new OnboardingViewModel([edition, new FakeWizardStep(WizardStepId.SignIn)]);
            await vm.PendingEnterForTesting;

            await edition.ChooseLocalCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;
            var afterChoice = vm.Current.Id;

            await vm.NextCommand.Execute().ToTask();

            return (edition.LocalPending, afterChoice, vm.Current.Id);
        });

        await Assert.That(pending).IsTrue();
        await Assert.That(afterChoice).IsEqualTo(WizardStepId.Edition);
        await Assert.That(afterNext).IsEqualTo(WizardStepId.Edition);
        await Assert.That(Edition(probe.Only("cli_setup_edition_chosen"))).IsEqualTo("local");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_is_inert_when_not_offered() {
        var probe = Probe();

        var choice = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, localOffered: false);
            await edition.OnEnterAsync(CancellationToken.None);
            await edition.ChooseLocalCommand.Execute().ToTask();

            return edition.Choice;
        });

        await Assert.That(choice).IsNull();
        await Assert.That(probe.Captured("cli_setup_edition_chosen")).IsEmpty();
        await Assert.That(probe.Only("cli_setup_edition_offered").Properties["local_offered"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Coming_back_offers_once_and_records_each_choice() {
        var probe = Probe();

        var current = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(probe.Funnel, localOffered: true);
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task The_wizard_opens_on_the_edition_choice_with_local_behind_its_gate(bool offered) {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options() with { LocalEditionOffered = offered });
            await graph.ViewModel.PendingEnterForTesting;

            var edition = graph.ViewModel.Current as EditionStepViewModel;
            await Assert.That(edition).IsNotNull();
            await Assert.That(edition!.LocalOffered).IsEqualTo(offered);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_shows_the_cards_in_place_of_next_and_the_cloud_card_reaches_sign_in() {
        var (localShown, cloudShown, signInShown, nextShown, after) = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(CliTelemetry.Disabled(TimeProvider.System).Funnel, localOffered: false);
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

            cloud.Command!.Execute(null);
            await vm.PendingAdvanceForTesting;
            Dispatcher.UIThread.RunJobs();
            var current = vm.Current.Id;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (local, cloudVisible, signIn, next, current);
        });

        await Assert.That(localShown ?? false).IsFalse();
        await Assert.That(cloudShown).IsTrue();
        await Assert.That(signInShown).IsTrue();
        await Assert.That(nextShown).IsFalse();
        await Assert.That(after).IsEqualTo(WizardStepId.SignIn);
    }
}
