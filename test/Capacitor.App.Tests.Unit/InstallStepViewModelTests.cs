using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class InstallStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    static readonly LocalEmbeddingModelManifest Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);

    static Func<CancellationToken, Task<LocalEmbeddingModelManifest?>> Offering(LocalEmbeddingModelManifest? offered) =>
        _ => Task.FromResult(offered);

    static InstallStepViewModel Step(LocalInstallController controller, string? appProfile = "default", string? active = "default",
        bool builtinOffered = true) =>
        new(controller, () => appProfile, () => active, () => true, Offering(builtinOffered ? Offered : null));

    static InstallStepViewModel Step(LocalInstallController controller, Func<CancellationToken, Task<ClientRepairOutcome>> repair) =>
        new(controller, () => "default", () => "default", () => true, Offering(Offered), repairClients: repair);

    /// Answers each repair with the next outcome, repeating the last.
    sealed class ScriptedRepair(params ClientRepairOutcome[] outcomes) {
        public int Calls { get; private set; }

        public Task<ClientRepairOutcome> RunAsync(CancellationToken ct) =>
            Task.FromResult(outcomes[Math.Min(Calls++, outcomes.Length - 1)]);
    }

    static async Task EnterAndInstallAsync(InstallStepViewModel install) {
        await install.OnEnterAsync(CancellationToken.None);
        await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
    }

    static LocalInstallController Controller(FakeKcapCli cli, LocalRoot root, TelemetryProbe probe, Action<Action>? post = null) =>
        new(() => cli, root, () => false, post ?? (action => action()), probe.Funnel);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_immediate_install_advances_once() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (current, advances) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            var advances = 0;
            install.AdvanceRequested += () => advances++;
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition), install,
                new FakeWizardStep(WizardStepId.Harnesses), new FakeWizardStep(WizardStepId.Done),
            ]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            await vm.NextCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;

            return (vm.Current.Id, advances);
        });

        await Assert.That(current).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(advances).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_install_advances_once() {
        var exit = new TaskCompletionSource<StreamingResult>();
        Action<StreamedLine>? print = null;
        var cli = new FakeKcapCli {
            SetupLocalBehavior = (onLine, _, _) => {
                print = onLine;
                return exit.Task;
            },
        };

        var (before, current, advances) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe(), action => Dispatcher.UIThread.Post(action));
            var install = Step(controller);
            var advances = 0;
            install.AdvanceRequested += () => advances++;
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition), install,
                new FakeWizardStep(WizardStepId.Harnesses), new FakeWizardStep(WizardStepId.Done),
            ]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask();
            await vm.NextCommand.Execute().ToTask();
            var before = vm.Current.Id;

            var settled = new TaskCompletionSource();
            controller.Settled += _ => settled.TrySetResult();
            print!(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            exit.SetResult(new StreamingResult(0, false, []));
            await settled.Task;
            await vm.PendingAdvanceForTesting;

            return (before, vm.Current.Id, advances);
        });

        await Assert.That(before).IsEqualTo(WizardStepId.Install);
        await Assert.That(current).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(advances).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Re_entry_after_a_failed_outcome_does_not_start_a_second_run() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };

        var (failureVisible, tryAgain) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            await EnterAndInstallAsync(Step(controller));

            // A reopened wizard builds a new page over the same controller.
            var reopened = Step(controller);
            await reopened.OnEnterAsync(CancellationToken.None);
            await reopened.OnEnterAsync(CancellationToken.None);

            return (reopened.FailureVisible, reopened.TryAgainVisible);
        });

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(1);
        await Assert.That(failureVisible).IsTrue();
        await Assert.That(tryAgain).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Locked_lifecycle_busy_offers_neither_retry_nor_back() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("lifecycle_busy")) };

        var (tryAgain, back, locked, message, canBack) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.PendingSetup(Config), Probe()));
            await EnterAndInstallAsync(install);

            return (install.TryAgainVisible, install.BackVisible, install.LockedVisible, install.FailureMessage,
                await install.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None));
        });

        await Assert.That(tryAgain).IsFalse();
        await Assert.That(back).IsFalse();
        await Assert.That(canBack).IsFalse();
        await Assert.That(locked).IsTrue();
        await Assert.That(message).IsEqualTo(InstallStepViewModel.BusyText);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permanent_refusal_while_locked_shows_the_cli_message() {
        const string cliMessage = "Port 5123 answers, but not as a Capacitor server.";
        var cli = new FakeKcapCli {
            SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("not_our_server", cliMessage)),
        };

        var (tryAgain, back, message) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.PendingSetup(Config), Probe()));
            await EnterAndInstallAsync(install);

            return (install.TryAgainVisible, install.BackVisible, install.FailureMessage);
        });

        await Assert.That(tryAgain).IsFalse();
        await Assert.That(back).IsFalse();
        await Assert.That(message).IsEqualTo(cliMessage);
    }

    /// A retryable failure's CLI message names the terminal command; the page leads with Try again instead
    /// and keeps the CLI's cause as the detail.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retryable_failure_leads_with_app_guidance_and_keeps_the_cli_cause_as_detail() {
        const string cliMessage = "The local server did not confirm it left setup mode. The setup journal is kept; run kcap setup --local again to finish it.";
        var cli = new FakeKcapCli {
            SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("commit_failed", cliMessage)),
        };

        var (message, detail, detailVisible) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.PendingSetup(Config), Probe()));
            await EnterAndInstallAsync(install);

            return (install.FailureMessage, install.FailureDetail, install.FailureDetailVisible);
        });

        await Assert.That(message).IsEqualTo(InstallStepViewModel.NotStartedText);
        await Assert.That(message!).DoesNotContain("kcap setup");
        await Assert.That(detail).IsEqualTo("The local server did not confirm it left setup mode. The setup journal is kept.");
        await Assert.That(detailVisible).IsTrue();
    }

    /// Every failure line the page shows comes from the installer, which addresses a terminal user.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("commit_failed", "Setup could not finish its commit (disk full). Nothing was removed; run kcap setup --local again to finish it.", "disk full")]
    [Arguments("recovery_failed", "The interrupted setup cannot be finished: /x/kcap-server is missing. The setup journal /x/journal.json is kept.", "kcap-server is missing")]
    [Arguments("registry_unreadable", "The installation registry /x/registry.json cannot be read (bad json). Fix or remove it, then run kcap setup --local again.", "cannot be read (bad json)")]
    public async Task No_displayed_failure_text_names_a_terminal_command(string code, string cliMessage, string cause) {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed(code, cliMessage)) };

        var shown = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.PendingSetup(Config), Probe()));
            await EnterAndInstallAsync(install);

            return string.Join("\n", new[] { install.FailureMessage, install.FailureDetail }.Where(t => t is not null));
        });

        await Assert.That(shown).DoesNotContain("kcap setup");
        await Assert.That(shown).DoesNotContain("run kcap");
        await Assert.That(shown).Contains(cause);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_profile_mismatch_settles_a_permanent_failure_without_a_child() {
        var probe = Probe();
        var cli = new FakeKcapCli();
        var controller = Controller(cli, LocalSetupScript.PendingSetup(Config), probe);

        var (refusal, tryAgain) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(controller, appProfile: "work", active: "personal");
            await install.OnEnterAsync(CancellationToken.None);

            return (install.RefusalMessage, install.TryAgainVisible);
        });

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Code).IsEqualTo("profile_mismatch");
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Permanent);
        await Assert.That(failed.LockedToLocal).IsTrue();
        await Assert.That(refusal).Contains("\"work\"");
        await Assert.That(tryAgain).IsFalse();
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(probe.Only("cli_setup_local_install_failed").Properties["code"]!.GetValue<string>()).IsEqualTo("profile_mismatch");
    }

    /// The app always passes both answers, so the installer's request for them names flags the user never typed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_missing_answer_failure_shows_the_app_headline_not_the_flags() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("answer_required",
                "A local installation needs both answers: that it serves only you (--single-user), and that you confirm it (--yes).")),
        };

        var (message, detail) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            await EnterAndInstallAsync(install);

            return (install.FailureMessage, install.FailureDetail);
        });

        await Assert.That(message).IsEqualTo(InstallStepViewModel.AnswerRequiredText);
        await Assert.That(detail).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unlocked_transient_failure_offers_retry_and_back_and_try_again_reruns() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };

        var (tryAgain, back, locked, canBack, runs) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), probe));
            await EnterAndInstallAsync(install);
            var shown = (install.TryAgainVisible, install.BackVisible, install.LockedVisible,
                await install.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None));

            await install.TryAgainCommand.Execute().ToTask();

            return (shown.Item1, shown.Item2, shown.Item3, shown.Item4, cli.SetupLocalCallCount);
        });

        await Assert.That(tryAgain).IsTrue();
        await Assert.That(back).IsTrue();
        await Assert.That(locked).IsFalse();
        await Assert.That(canBack).IsTrue();
        await Assert.That(runs).IsEqualTo(2);
        await Assert.That(probe.Names).IsEquivalentTo([
            "cli_setup_local_install_started", "cli_setup_local_install_failed",
            "cli_setup_local_install_retried",
            "cli_setup_local_install_started", "cli_setup_local_install_failed",
        ], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task While_running_cancel_is_offered_and_back_is_not() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = async (onLine, interrupt, _) => {
                var exited = new TaskCompletionSource();
                await using var reg = interrupt.Register(() => exited.TrySetResult());
                await exited.Task;
                onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("cancelled")));
                return new StreamingResult(130, false, []);
            },
        };

        var (cancel, back, canBack, canNext, after) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            var install = Step(controller);
            await EnterAndInstallAsync(install);
            var shown = (install.CancelVisible, install.BackVisible,
                await install.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None),
                await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None));

            await install.CancelCommand.Execute().ToTask();
            await controller.SettleAsync();

            return (shown.Item1, shown.Item2, shown.Item3, shown.Item4, install.TryAgainVisible);
        });

        await Assert.That(cancel).IsTrue();
        await Assert.That(back).IsFalse();
        await Assert.That(canBack).IsFalse();
        await Assert.That(canNext).IsFalse();
        await Assert.That(after).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Profile_override_refuses_before_starting() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (refusal, visible) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), appProfile: "work", active: "personal");
            await install.OnEnterAsync(CancellationToken.None);

            return (install.RefusalMessage, install.RefusalVisible);
        });

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(visible).IsTrue();
        await Assert.That(refusal).Contains("\"work\"");
        await Assert.That(refusal).Contains("\"personal\"");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pass_through_off_the_local_path_and_once_installed() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };
        var onLocal = false;

        var (offPath, onPathBefore, onPathAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            var install = new InstallStepViewModel(controller, () => null, () => null, () => onLocal, Offering(Offered));
            var offPath = install.PassThrough;
            onLocal = true;
            var before = install.PassThrough;
            await EnterAndInstallAsync(install);

            return (offPath, before, install.PassThrough);
        });

        await Assert.That(offPath).IsTrue();
        await Assert.That(onPathBefore).IsFalse();
        await Assert.That(onPathAfter).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_shell_hides_back_while_the_install_runs_and_shows_it_after_an_unlocked_failure() {
        var exit = new TaskCompletionSource<StreamingResult>();
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => exit.Task };

        var (running, failed) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            var install = Step(controller);
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition), install, new FakeWizardStep(WizardStepId.Done),
            ]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask();
            await vm.NextCommand.Execute().ToTask();
            var running = vm.BackVisible;

            exit.SetResult(new StreamingResult(1, false, []));
            await controller.SettleAsync();

            return (running, vm.BackVisible);
        });

        await Assert.That(running).IsFalse();
        await Assert.That(failed).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entry_asks_about_search_and_starts_nothing_until_install() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (choice, progress, runsBefore) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            await install.OnEnterAsync(CancellationToken.None);
            var shown = (install.ChoiceVisible, install.ProgressVisible, cli.SetupLocalCallCount);

            install.Search.UseKeyword = true;
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return shown;
        });

        await Assert.That(choice).IsTrue();
        await Assert.That(progress).IsFalse();
        await Assert.That(runsBefore).IsEqualTo(0);
        await Assert.That(cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.KeywordOnly], CollectionOrdering.Matching);
    }

    /// A stack whose manifest names no built-in model cannot honour that choice, so the page neither offers nor
    /// recommends it, and an unchosen run still passes no flag.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task The_builtin_option_and_recommendation_follow_what_the_stack_offers(bool offered) {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (enabled, builtin, status, description) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), builtinOffered: offered);
            await install.OnEnterAsync(CancellationToken.None);
            var shown = (install.Search.BuiltinEnabled, install.Search.UseBuiltin, install.Search.BuiltinStatus, install.Search.BuiltinDescription);

            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return shown;
        });

        await Assert.That(enabled).IsEqualTo(offered);
        await Assert.That(builtin).IsEqualTo(offered);
        await Assert.That(status is null).IsEqualTo(offered);
        if (offered) await Assert.That(description).Contains("(about 300 MB)");
        await Assert.That(cli.SetupLocalEmbeddings.Single().Args).IsEmpty();
        await Assert.That(cli.SetupLocalEmbeddings.Single().Provider)
            .IsEqualTo(offered ? LocalEmbeddingProvider.Builtin : LocalEmbeddingProvider.None);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_choice_starts_from_the_recorded_model_and_a_retry_keeps_it() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };

        var (other, model) = await AvaloniaSession.DispatchAsync(async () => {
            var install = new InstallStepViewModel(
                Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), () => "default", () => "default", () => true, Offering(Offered),
                () => LocalEmbeddingSetting.Ollama("nomic-embed-text"));
            await EnterAndInstallAsync(install);
            await install.TryAgainCommand.Execute().ToTask();

            return (install.Search.UseOther, install.Search.OtherModel);
        });

        await Assert.That(other).IsTrue();
        await Assert.That(model).IsEqualTo("nomic-embed-text");
        await Assert.That(cli.SetupLocalEmbeddings).IsEquivalentTo(
            [new LocalEmbeddingChoice("nomic-embed-text"), new LocalEmbeddingChoice("nomic-embed-text")], CollectionOrdering.Matching);
    }

    /// An interrupted setup may have stopped before it recorded the answer; resuming with the default would replace it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_setup_with_no_recorded_answer_asks_again() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var choice = await AvaloniaSession.DispatchAsync(async () => {
            var install = new InstallStepViewModel(
                Controller(cli, LocalSetupScript.PendingSetup(Config, recorded: false), Probe()), () => "default", () => "default",
                () => true, Offering(Offered), () => null, resuming: () => true);
            await install.OnEnterAsync(CancellationToken.None);

            return install.ChoiceVisible;
        });

        await Assert.That(choice).IsTrue();
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_setup_resumes_with_its_recorded_answer() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var choice = await AvaloniaSession.DispatchAsync(async () => {
            var install = new InstallStepViewModel(
                Controller(cli, LocalSetupScript.PendingSetup(Config), Probe()), () => "default", () => "default",
                () => true, Offering(Offered), () => LocalEmbeddingSetting.Declined, resuming: () => true);
            await install.OnEnterAsync(CancellationToken.None);

            return install.ChoiceVisible;
        });

        await Assert.That(choice).IsFalse();
        await Assert.That(cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.KeywordOnly], CollectionOrdering.Matching);
    }

    /// The shell's headline binds the step's Title, so each change of the choice has to be announced.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refused_arguments_reopen_the_choice() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(2, LocalSetupScript.Failed("invalid_arguments")) };

        var (choice, headlines) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            var announced = new List<string>();
            install.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(InstallStepViewModel.Title)) announced.Add(install.Title);
            };
            await EnterAndInstallAsync(install);

            return (install.ChoiceVisible, announced);
        });

        await Assert.That(choice).IsTrue();
        await Assert.That(headlines).IsEquivalentTo(
            ["One more step before installing", $"Installing kcap on {ThisDevice.Name}", "One more step before installing"],
            CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("")]
    [Arguments("my model")]
    [Arguments("-rm")]
    public async Task Another_model_the_cli_would_refuse_cannot_install(string name) {
        var cli = new FakeKcapCli();

        var (canInstall, error) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            await install.OnEnterAsync(CancellationToken.None);
            install.Search.UseOllama  = true;
            install.Search.UseOther   = true;
            install.Search.OtherModel = name;

            return (install.CanContinue, install.Search.OtherModelError);
        });

        await Assert.That(canInstall).IsFalse();
        await Assert.That(error is null).IsEqualTo(name.Length == 0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_older_kcap_is_updated_before_anything_is_asked() {
        var repair = new TaskCompletionSource<ClientRepairOutcome>();
        var cli = new FakeKcapCli();

        var (during, after) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), _ => repair.Task);
            var entering = install.OnEnterAsync(CancellationToken.None);
            var during = (install.Repairing, install.ChoiceVisible, install.ProgressVisible, install.BackVisible);

            repair.SetResult(new ClientRepairOutcome.Repaired("/usr/local/bin/kcap"));
            await entering;

            return (during, (install.Repairing, install.ChoiceVisible, install.ClientProblemVisible));
        });

        await Assert.That(during).IsEqualTo((true, false, false, false));
        await Assert.That(after).IsEqualTo((false, true, false));
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [MethodDataSource(nameof(Unrepaired))]
    public async Task An_older_kcap_left_in_place_asks_nothing_and_try_again_repairs_again(ClientRepairOutcome first, string named) {
        var repair = new ScriptedRepair(first, new ClientRepairOutcome.Clear());
        var cli = new FakeKcapCli();

        var (problem, shown, after) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), repair.RunAsync);
            await install.OnEnterAsync(CancellationToken.None);
            var shown = (install.ClientProblemVisible, install.ChoiceVisible, install.ProgressVisible, install.BackVisible);
            var problem = install.ClientProblem;

            await install.RepairAgainCommand.Execute().ToTask();

            return (problem, shown, (install.ClientProblemVisible, install.ChoiceVisible));
        });

        await Assert.That(shown).IsEqualTo((true, false, false, true));
        await Assert.That(problem).Contains(named);
        await Assert.That(problem!).DoesNotContain("kcap setup");
        await Assert.That(after).IsEqualTo((false, true));
        await Assert.That(repair.Calls).IsEqualTo(2);
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
    }

    public static IEnumerable<Func<(ClientRepairOutcome, string)>> Unrepaired() {
        yield return () => (new ClientRepairOutcome.Cancelled("/usr/local/bin/kcap"), "/usr/local/bin/kcap");
        yield return () => (new ClientRepairOutcome.Failed("/usr/local/bin/kcap", "disk full."), "disk full.");
        yield return () => (new ClientRepairOutcome.OldApp("/Users/me/Applications/kcap.app/Contents/MacOS/kcap"), "/Users/me/Applications/kcap.app");
    }

    /// Back, then the page again, is how a user leaves a refused run: it must check again rather than replay the
    /// refusal, and must not ask the search question a second time.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Re_entry_after_an_older_kcap_refusal_checks_again_and_installs_with_the_same_answer() {
        var cli = new FakeKcapCli { SetupLocalBehavior = RefusedThenCommitted() };
        var repair = new ScriptedRepair(new ClientRepairOutcome.Clear());

        var (refused, outcome, choice) = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            var install = Step(controller, repair.RunAsync);
            await EnterAndInstallAsync(install);
            var refused = (install.FailureMessage, install.TryAgainVisible);

            var reopened = Step(controller, repair.RunAsync);
            await reopened.OnEnterAsync(CancellationToken.None);

            return (refused, controller.Outcome, reopened.ChoiceVisible);
        });

        await Assert.That(refused).IsEqualTo((InstallStepViewModel.OldClientText, true));
        await Assert.That(outcome).IsTypeOf<LocalInstallOutcome.Installed>();
        await Assert.That(choice).IsFalse();
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(2);
        await Assert.That(repair.Calls).IsEqualTo(3);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Try_again_after_an_older_kcap_refusal_repairs_before_rerunning() {
        var cli = new FakeKcapCli { SetupLocalBehavior = RefusedThenCommitted() };
        var repair = new ScriptedRepair(new ClientRepairOutcome.Clear());

        var outcome = await AvaloniaSession.DispatchAsync(async () => {
            var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
            var install = Step(controller, repair.RunAsync);
            await EnterAndInstallAsync(install);

            await install.TryAgainCommand.Execute().ToTask();

            return controller.Outcome;
        });

        await Assert.That(outcome).IsTypeOf<LocalInstallOutcome.Installed>();
        await Assert.That(repair.Calls).IsEqualTo(3);
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(2);
    }

    /// Where the app has no repair (Windows), trying again cannot clear an older kcap, so the page says what the
    /// installer said: which copy, and to update or remove it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_repair_an_older_kcap_refusal_names_the_copy_to_update() {
        var cli = new FakeKcapCli { SetupLocalBehavior = RefusedThenCommitted() };

        var message = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            await EnterAndInstallAsync(install);
            return install.FailureMessage;
        });

        await Assert.That(message).Contains("/usr/local/bin/kcap");
        await Assert.That(message).Contains("Update or remove it");
    }

    static Func<Action<StreamedLine>, CancellationToken, CancellationToken, Task<StreamingResult>> RefusedThenCommitted() {
        var refused   = LocalSetupScript.Prints(1, LocalSetupScript.Failed("client_below_floor",
            "The kcap on PATH (/usr/local/bin/kcap) is 1.3.0-beta.8, and a local installation needs a kcap newer than 1.3.0. Update or remove it before setting up a local installation."));
        var committed = LocalSetupScript.Prints(0, LocalSetupScript.Committed);
        var runs      = 0;

        return (onLine, interrupt, ct) => (runs++ == 0 ? refused : committed)(onLine, interrupt, ct);
    }

    /// The choice can stay up across Back and a return to the page, so Install checks again before it starts anything.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_from_the_choice_checks_for_an_older_kcap_again() {
        var repair = new ScriptedRepair(new ClientRepairOutcome.Clear(), new ClientRepairOutcome.Cancelled("/usr/local/bin/kcap"));
        var cli    = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };

        var (problem, choice) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), repair.RunAsync);
            await EnterAndInstallAsync(install);

            return (install.ClientProblemVisible, install.ChoiceVisible);
        });

        await Assert.That(problem).IsTrue();
        await Assert.That(choice).IsFalse();
        await Assert.That(repair.Calls).IsEqualTo(2);
        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
    }

    /// After refused arguments the controller still holds the earlier run's answer, so Try again on a blocked Install
    /// must start with what the user chose this time.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Try_again_on_a_blocked_install_starts_with_the_answer_chosen_now() {
        var repair  = new ScriptedRepair(new ClientRepairOutcome.Clear(), new ClientRepairOutcome.Clear(),
            new ClientRepairOutcome.Cancelled("/usr/local/bin/kcap"), new ClientRepairOutcome.Clear());
        var runs    = 0;
        var refused = LocalSetupScript.Prints(2, LocalSetupScript.Failed("invalid_arguments"));
        var cli     = new FakeKcapCli {
            SetupLocalBehavior = (onLine, interrupt, ct) =>
                (runs++ == 0 ? refused : LocalSetupScript.Prints(0, LocalSetupScript.Committed))(onLine, interrupt, ct),
        };

        var (problem, started) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), repair.RunAsync);
            await EnterAndInstallAsync(install);
            install.Search.UseKeyword = true;
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var problem = install.ClientProblemVisible;

            await install.RepairAgainCommand.Execute().ToTask();

            return (problem, cli.SetupLocalEmbeddings.ToList());
        });

        await Assert.That(problem).IsTrue();
        await Assert.That(started.Count).IsEqualTo(2);
        await Assert.That(started[1]).IsEqualTo(LocalEmbeddingChoice.KeywordOnly);
        await Assert.That(started[0]).IsNotEqualTo(LocalEmbeddingChoice.KeywordOnly);
    }
}
