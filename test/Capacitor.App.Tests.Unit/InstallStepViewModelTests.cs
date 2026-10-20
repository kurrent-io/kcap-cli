using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class InstallStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    static InstallStepViewModel Step(LocalInstallController controller, string? appProfile = "default", string? active = "default") =>
        new(controller, () => appProfile, () => active, () => true);

    static async Task EnterAndInstallAsync(InstallStepViewModel install) {
        await install.OnEnterAsync(CancellationToken.None);
        await install.InstallCommand.Execute().ToTask();
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
            await install.InstallCommand.Execute().ToTask();
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
            await install.InstallCommand.Execute().ToTask();
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
        await Assert.That(message!).DoesNotContain("kcap ");
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
            var install = new InstallStepViewModel(controller, () => null, () => null, () => onLocal);
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
            await install.InstallCommand.Execute().ToTask();
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
            await install.InstallCommand.Execute().ToTask();

            return shown;
        });

        await Assert.That(choice).IsTrue();
        await Assert.That(progress).IsFalse();
        await Assert.That(runsBefore).IsEqualTo(0);
        await Assert.That(cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.KeywordOnly], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_choice_starts_from_the_recorded_model_and_a_retry_keeps_it() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };

        var (other, model) = await AvaloniaSession.DispatchAsync(async () => {
            var install = new InstallStepViewModel(
                Controller(cli, LocalSetupScript.NoSetup(Config), Probe()), () => "default", () => "default", () => true,
                () => new LocalEmbeddingSetting("nomic-embed-text"));
            await EnterAndInstallAsync(install);
            await install.TryAgainCommand.Execute().ToTask();

            return (install.Search.UseOther, install.Search.OtherModel);
        });

        await Assert.That(other).IsTrue();
        await Assert.That(model).IsEqualTo("nomic-embed-text");
        await Assert.That(cli.SetupLocalEmbeddings).IsEquivalentTo(
            [new LocalEmbeddingChoice("nomic-embed-text"), new LocalEmbeddingChoice("nomic-embed-text")], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Another_model_with_no_name_cannot_install() {
        var cli = new FakeKcapCli();

        var canInstall = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli, LocalSetupScript.NoSetup(Config), Probe()));
            await install.OnEnterAsync(CancellationToken.None);
            install.Search.UseOther = true;

            return await install.InstallCommand.CanExecute.FirstAsync().ToTask();
        });

        await Assert.That(canInstall).IsFalse();
    }
}
