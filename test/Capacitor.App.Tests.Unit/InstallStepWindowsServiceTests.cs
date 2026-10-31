using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

public class InstallStepWindowsServiceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Declined    = """{"v":1,"event":"windows_service_declined"}""";
    const string Unavailable = """{"v":1,"event":"windows_service_unavailable"}""";

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    LocalInstallController Controller(FakeKcapCli cli, Func<Task>? rememberDeclined = null) =>
        new(() => cli, LocalSetupScript.NoSetup(Config), () => false, action => action(), Probe().Funnel,
            rememberWindowsServiceDeclined: rememberDeclined);

    static InstallStepViewModel Step(
            LocalInstallController controller, WindowsServiceOffer? offer, SearchChoiceViewModel? search = null) =>
        new(controller, () => "default", () => "default", () => true, _ => Task.FromResult<LocalEmbeddingModelManifest?>(null),
            windowsServiceOffer: offer is { } o ? _ => Task.FromResult(o) : null, search: search);

    sealed class AppStates(AppState state) : IAppStateStore {
        public AppState State = state;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            State = mutate(State);
            return Task.FromResult(true);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(WindowsServiceOffer.On, true, true, true)]
    [Arguments(WindowsServiceOffer.Off, true, false, false)]
    [Arguments(null, false, false, false)]
    public async Task The_choice_offers_the_service_as_told_and_install_asks_for_it_only_when_ticked(
            WindowsServiceOffer? offer, bool visible, bool ticked, bool asked) {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (shownVisible, shownTicked) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), offer);
            await install.OnEnterAsync(CancellationToken.None);
            var shown = (install.WindowsServiceVisible, install.RunAsWindowsService);
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return shown;
        });

        await Assert.That(shownVisible).IsEqualTo(visible);
        await Assert.That(shownTicked).IsEqualTo(ticked);
        await Assert.That(cli.SetupLocalWindowsService).IsEquivalentTo([asked]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unticking_the_service_installs_without_it() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), WindowsServiceOffer.On);
            await install.OnEnterAsync(CancellationToken.None);
            install.RunAsWindowsService = false;
            return await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        });

        await Assert.That(cli.SetupLocalWindowsService).IsEquivalentTo([false]);
    }

    /// The decline is remembered, so the app's own service offer does not ask again, and a retry of the same
    /// install goes ahead without the prompt.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_declined_prompt_is_remembered_and_the_page_says_kcap_runs_while_signed_in() {
        var remembered = 0;
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, Declined, LocalSetupScript.Failed("download_failed")) };

        var (note, retryAsks) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, () => {
                remembered++;
                return Task.CompletedTask;
            });
            var install = Step(controller, WindowsServiceOffer.On);
            await install.OnEnterAsync(CancellationToken.None);
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (install.WindowsServiceNoteVisible ? install.WindowsServiceNote : null, controller.WindowsService);
        });

        await Assert.That(remembered).IsEqualTo(1);
        await Assert.That(note).IsEqualTo(InstallStepViewModel.WindowsServiceDeclinedNote);
        await Assert.That(retryAsks).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unavailable_service_is_noted_but_not_remembered_as_declined() {
        var remembered = 0;
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, Unavailable, LocalSetupScript.Committed) };

        var note = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, () => {
                remembered++;
                return Task.CompletedTask;
            }), WindowsServiceOffer.On);
            await install.OnEnterAsync(CancellationToken.None);
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return install.WindowsServiceNote;
        });

        await Assert.That(remembered).IsEqualTo(0);
        await Assert.That(note).IsEqualTo(InstallStepViewModel.WindowsServiceUnavailableNote);
    }

    /// Keyword search needs no Ollama, so only the other answers wait for the check.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_is_open_during_the_ollama_check_only_while_keyword_search_is_chosen() {
        var ollama = new TaskCompletionSource<bool>();

        var (checking, keyword, ollamaPicked) = await AvaloniaSession.DispatchAsync(async () => {
            var search = new SearchChoiceViewModel(ollamaAvailable: _ => ollama.Task, ollamaInstalled: () => false);
            var install = Step(Controller(new FakeKcapCli()), null, search);
            await install.OnEnterAsync(CancellationToken.None);
            _ = search.CheckOllamaCommand.Execute().ToTask();
            for (var i = 0; i < 100 && !search.Checking; i++) await Task.Delay(10);

            var keyword = (search.UseKeyword, install.CanContinue);
            search.UseOllama = true;
            var ollamaPicked = install.CanContinue;

            return (search.Checking, keyword, ollamaPicked);
        });
        ollama.SetResult(false);

        await Assert.That(checking).IsTrue();
        await Assert.That(keyword).IsEqualTo((true, true));
        await Assert.That(ollamaPicked).IsFalse();
    }

    [Test]
    [Arguments("windows_service", false, false, WindowsServiceOffer.Hidden)]
    [Arguments("windows_service", true, false, WindowsServiceOffer.On)]
    [Arguments("windows_scheduled_task", null, false, WindowsServiceOffer.On)]
    [Arguments("windows_scheduled_task", null, true, WindowsServiceOffer.Off)]
    [Arguments(null, null, false, WindowsServiceOffer.On)]
    public async Task The_offer_hides_behind_a_current_service_and_starts_unticked_after_a_decline(
            string? supervisor, bool? upgrade, bool declined, WindowsServiceOffer expected) {
        var cli = new FakeKcapCli {
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(supervisor is null ? null : new ServiceSnapshot(
                "daemon", true, "running", null, null, null, 1, false, false, Supervisor: supervisor, ServiceUpgradeAvailable: upgrade)),
        };

        var offer = await WizardComposition.WindowsServiceOfferAsync(
            new AppStates(new AppState { WindowsServiceDeclined = declined }), cli, CancellationToken.None);

        await Assert.That(offer).IsEqualTo(expected);
    }
}
