using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using AppUnderTest = Capacitor.App.App;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

sealed class FakeWizardStep(WizardStepId id, string? title = null) : IWizardStep {
    public WizardStepId Id { get; } = id;
    public string Title { get; } = title ?? id.ToString();
    public bool Applicable { get; init; } = true;
    public bool Satisfied { get; set; }
    public bool PassThrough { get; set; }
    public int EnterCount { get; private set; }
    public bool ThrowOnEnter { get; init; }
    public bool AdvanceOnEnter { get; init; }
    public Func<WizardNavigation, CancellationToken, Task<bool>>? CanLeave { get; init; }

    public event Action? AdvanceRequested;

    public Task OnEnterAsync(CancellationToken ct) {
        EnterCount++;
        if (AdvanceOnEnter) AdvanceRequested?.Invoke();
        if (ThrowOnEnter) throw new InvalidOperationException("boom: enter failed");
        return Task.CompletedTask;
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) =>
        CanLeave?.Invoke(direction, ct) ?? Task.FromResult(true);
}

/// Dispatches through the real headless session (ConsentPromptViewModelTests' pattern) — the
/// constructor's WhenAnyValue call trips ReactiveUI's init guard under WithImmediateRxScheduler.
public class OnboardingViewModelTests {
    static bool CanExecute(ReactiveCommand<ReactiveUnit, ReactiveUnit> command) {
        var value = false;
        using var sub = command.CanExecute.Subscribe(v => value = v);
        return value;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Steps_excludes_non_applicable_entries_and_starts_on_the_first_applicable_one() {
        var (stepIds, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var shim = new FakeWizardStep(WizardStepId.Harnesses) { Applicable = false };
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([shim, connect, done]);
            await vm.PendingEnterForTesting;

            return (vm.Steps.Select(s => s.Id).ToList(), vm.Current.Id);
        });

        await Assert.That(stepIds).IsEquivalentTo([WizardStepId.Edition, WizardStepId.Done], CollectionOrdering.Matching);
        await Assert.That(currentId).IsEqualTo(WizardStepId.Edition);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_back_and_skip_move_through_the_full_step_order() {
        var (afterNext, afterSkip, afterBack1, afterBack2) = await AvaloniaSession.DispatchAsync(async () => {
            var steps = new[] {
                new FakeWizardStep(WizardStepId.Edition),
                new FakeWizardStep(WizardStepId.SignIn),
                new FakeWizardStep(WizardStepId.Done),
            };
            var vm = new OnboardingViewModel(steps);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            var afterNext = vm.Current.Id;

            await vm.SkipCommand.Execute().ToTask();
            var afterSkip = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask();
            var afterBack1 = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask();
            var afterBack2 = vm.Current.Id;

            return (afterNext, afterSkip, afterBack1, afterBack2);
        });

        await Assert.That(afterNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterSkip).IsEqualTo(WizardStepId.Done);
        await Assert.That(afterBack1).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterBack2).IsEqualTo(WizardStepId.Edition);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_steps_over_a_pass_through_step_and_back_skips_it_too() {
        var (afterNext, entered, afterBack) = await AvaloniaSession.DispatchAsync(async () => {
            var history = new FakeWizardStep(WizardStepId.Import) { PassThrough = true };
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.Harnesses), history, new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            var afterNext = vm.Current.Id;
            var entered = history.EnterCount;

            await vm.BackCommand.Execute().ToTask();
            return (afterNext, entered, vm.Current.Id);
        });

        await Assert.That(afterNext).IsEqualTo(WizardStepId.Done);
        await Assert.That(entered).IsEqualTo(0);
        await Assert.That(afterBack).IsEqualTo(WizardStepId.Harnesses);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_advance_raised_during_entry_is_not_lost() {
        var (current, entered) = await AvaloniaSession.DispatchAsync(async () => {
            var last = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition),
                new FakeWizardStep(WizardStepId.Import) { AdvanceOnEnter = true },
                last]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;
            return (vm.Current.Id, last.EnterCount);
        });

        await Assert.That(current).IsEqualTo(WizardStepId.Done);
        await Assert.That(entered).IsEqualTo(1);
    }

    /// The counter counts pages this run shows, from the first page that offers a choice; a page shown before it
    /// began passing through keeps its place.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_step_counter_counts_only_pages_this_run_shows() {
        var (start, first, second, afterPassing) = await AvaloniaSession.DispatchAsync(async () => {
            var install = new FakeWizardStep(WizardStepId.Install);
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition) { PassThrough = true },
                install,
                new FakeWizardStep(WizardStepId.SignIn) { PassThrough = true },
                new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;
            var start = vm.Current.Id;
            var first = vm.Eyebrow;

            await vm.NextCommand.Execute().ToTask();
            var second = vm.Eyebrow;
            install.PassThrough = true;

            return (start, first, second, vm.Eyebrow);
        });

        await Assert.That(start).IsEqualTo(WizardStepId.Install);
        await Assert.That(first).StartsWith("STEP 1 OF 2 ·");
        await Assert.That(second).StartsWith("STEP 2 OF 2 ·");
        await Assert.That(afterPassing).StartsWith("STEP 2 OF 2 ·");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Back_skips_pass_through_steps() {
        var current = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Edition),
                new FakeWizardStep(WizardStepId.Import) { PassThrough = true },
                new FakeWizardStep(WizardStepId.Done)], startAt: WizardStepId.Done);
            await vm.PendingEnterForTesting;

            await vm.BackCommand.Execute().ToTask();
            return vm.Current.Id;
        });

        await Assert.That(current).IsEqualTo(WizardStepId.Edition);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Back_is_hidden_when_every_earlier_step_passes_through() {
        var (visible, canBack) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Import) { PassThrough = true },
                new FakeWizardStep(WizardStepId.Done)], startAt: WizardStepId.Done);
            await vm.PendingEnterForTesting;

            return (vm.BackVisible, CanExecute(vm.BackCommand));
        });

        await Assert.That(visible).IsFalse();
        await Assert.That(canBack).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Finish_later_follows_its_gate() {
        var (open, closed) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new System.Reactive.Subjects.BehaviorSubject<bool>(true);
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.Edition)], canFinishLater: gate);
            await vm.PendingEnterForTesting;

            var open = (CanExecute(vm.FinishLaterCommand), vm.FinishLaterVisible);
            gate.OnNext(false);
            return (open, (CanExecute(vm.FinishLaterCommand), vm.FinishLaterVisible));
        });

        await Assert.That(open).IsEqualTo((true, true));
        await Assert.That(closed).IsEqualTo((false, false));
    }

    /// Done's own primary closes setup, so Finish later shows there only while the service is not running,
    /// and follows it as it changes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Finish_later_shows_on_the_done_page_only_while_the_service_is_not_running() {
        var (onEdition, failed, running) = await AvaloniaSession.DispatchAsync(async () => {
            var facts = DoneFacts.Empty with { DaemonProblem = "The daemon install failed (verify_viability)." };
            var changed = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
            var done = new DoneStepViewModel(() => facts, restate: changed);
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.Edition), done]);
            await vm.PendingEnterForTesting;

            var onEdition = vm.FinishLaterVisible;
            await vm.NextCommand.Execute().ToTask();
            var failed = vm.FinishLaterVisible;
            facts = facts with { DaemonRunning = true, DaemonProblem = null };
            changed.OnNext(System.Reactive.Unit.Default);
            return (onEdition, failed, vm.FinishLaterVisible);
        });

        await Assert.That(onEdition).IsTrue();
        await Assert.That(failed).IsTrue();
        await Assert.That(running).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_last_step_is_shown_even_when_it_could_pass_through() {
        var current = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([
                new FakeWizardStep(WizardStepId.Import), new FakeWizardStep(WizardStepId.Done) { PassThrough = true }]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask();
            return vm.Current.Id;
        });

        await Assert.That(current).IsEqualTo(WizardStepId.Done);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_false_holds_the_current_step_for_every_direction() {
        var (afterNext, afterVetoedNext, afterVetoedSkip, afterVetoedBack) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { CanLeave = (_, _) => Task.FromResult(false) };
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // Connect -> SignIn, allowed
            var afterNext = vm.Current.Id;

            await vm.NextCommand.Execute().ToTask(); // vetoed
            var afterVetoedNext = vm.Current.Id;

            await vm.SkipCommand.Execute().ToTask(); // vetoed
            var afterVetoedSkip = vm.Current.Id;

            await vm.BackCommand.Execute().ToTask(); // vetoed
            var afterVetoedBack = vm.Current.Id;

            return (afterNext, afterVetoedNext, afterVetoedSkip, afterVetoedBack);
        });

        await Assert.That(afterNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedNext).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedSkip).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(afterVetoedBack).IsEqualTo(WizardStepId.SignIn);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_fires_on_initial_entry_and_again_on_re_entry() {
        var (connectEntersInitially, signInEntersInitially, signInEntersAfterNext, connectEntersAfterBack) =
            await AvaloniaSession.DispatchAsync(async () => {
                var connect = new FakeWizardStep(WizardStepId.Edition);
                var signIn = new FakeWizardStep(WizardStepId.SignIn);
                var vm = new OnboardingViewModel([connect, signIn]);
                await vm.PendingEnterForTesting;

                var connectEntersInitially = connect.EnterCount;
                var signInEntersInitially = signIn.EnterCount;

                await vm.NextCommand.Execute().ToTask();
                var signInEntersAfterNext = signIn.EnterCount;

                await vm.BackCommand.Execute().ToTask();
                var connectEntersAfterBack = connect.EnterCount;

                return (connectEntersInitially, signInEntersInitially, signInEntersAfterNext, connectEntersAfterBack);
            });

        await Assert.That(connectEntersInitially).IsEqualTo(1);
        await Assert.That(signInEntersInitially).IsEqualTo(0);
        await Assert.That(signInEntersAfterNext).IsEqualTo(1);
        await Assert.That(connectEntersAfterBack).IsEqualTo(2); // re-entry
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Back_is_disabled_on_the_first_step_and_skip_is_disabled_on_the_last_step() {
        var (backOnFirst, skipOnFirst, nextOnFirst, skipVisibleOnFirst, backOnLast, skipOnLast, nextOnLast, skipVisibleOnLast) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, done]);
            await vm.PendingEnterForTesting;

            var backOnFirst = CanExecute(vm.BackCommand);
            var skipOnFirst = CanExecute(vm.SkipCommand);
            var nextOnFirst = vm.NextLabel;
            var skipVisibleOnFirst = vm.SkipVisible;

            await vm.NextCommand.Execute().ToTask(); // -> Done

            var backOnLast = CanExecute(vm.BackCommand);
            var skipOnLast = CanExecute(vm.SkipCommand);

            return (backOnFirst, skipOnFirst, nextOnFirst, skipVisibleOnFirst, backOnLast, skipOnLast, vm.NextLabel, vm.SkipVisible);
        });

        await Assert.That(backOnFirst).IsFalse();
        await Assert.That(skipOnFirst).IsTrue();
        await Assert.That(nextOnFirst).IsEqualTo("Next");
        await Assert.That(skipVisibleOnFirst).IsTrue();
        await Assert.That(backOnLast).IsTrue();
        await Assert.That(skipOnLast).IsFalse();
        await Assert.That(nextOnLast).IsEqualTo("Get started");
        await Assert.That(skipVisibleOnLast).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_on_the_last_step_finishes_and_raises_CloseRequested_exactly_once() {
        var (finalStepId, closeCount) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, done]);
            await vm.PendingEnterForTesting;

            var count = 0;
            vm.CloseRequested += () => count++;

            await vm.NextCommand.Execute().ToTask(); // Connect -> Done
            await vm.NextCommand.Execute().ToTask(); // finish

            return (vm.Current.Id, count);
        });

        await Assert.That(finalStepId).IsEqualTo(WizardStepId.Done);
        await Assert.That(closeCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Finishing_later_closes_navigation_and_a_late_close_waiter_does_not_hang() {
        await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.Harnesses), new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;
            await vm.FinishLaterCommand.Execute().ToTask();
            await AppUnderTest.WaitForWizardCloseAsync(vm, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.That(vm.CanGoNext).IsFalse();
            await Assert.That(vm.TryGoTo(WizardStepId.Done)).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_close_raises_CloseRequested() {
        var closeCount = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var vm = new OnboardingViewModel([connect]);
            await vm.PendingEnterForTesting;

            var count = 0;
            vm.CloseRequested += () => count++;

            var (desktop, _) = FakeClassicDesktopLifetime.Create();
            var window = AppUnderTest.ShowWizardWindow(desktop, vm);
            Dispatcher.UIThread.RunJobs();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return count;
        });

        await Assert.That(closeCount).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_renders_the_current_steps_title() {
        var rendered = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition, "Connect to kcap");
            var vm = new OnboardingViewModel([connect]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            var title = texts.FirstOrDefault(t => t.Name == "StepTitleText");
            var eyebrow = texts.FirstOrDefault(t => t.Name == "StepEyebrowText");

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (title?.Text, title?.Classes.Contains("onboardingHeadline"), eyebrow?.Text);
        });

        await Assert.That(rendered.Item1).IsEqualTo("Connect to kcap");
        await Assert.That(rendered.Item2).IsTrue();
        await Assert.That(rendered.Item3).IsEqualTo("STEP 1 OF 1 · CONNECT TO KCAP");
    }

    // ── Busy gate and veto handling ──────────────────────────

    /// Skip suspends on a pending veto check; a directly-invoked Next (bypassing
    /// the canExecute-driven Button disable a real UI enforces) must be a silent no-op — the
    /// shared busy gate inside NavigateAsync itself, not just canExecute — rather than racing
    /// Skip's own _index mutation once the veto later resolves.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Skip_blocked_on_a_pending_veto_cannot_race_a_concurrent_Next() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>();
            var connect = new FakeWizardStep(WizardStepId.Edition) {
                CanLeave = (direction, _) => direction == WizardNavigation.Skip ? gate.Task : Task.FromResult(true),
            };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            var skipTask = vm.SkipCommand.Execute().ToTask(); // starts, suspends on gate.Task
            await vm.NextCommand.Execute().ToTask(); // must be a silent no-op — no exception

            gate.SetResult(true); // release Skip's veto check
            await skipTask;

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // exactly one transition, no double-increment
    }

    /// The busy gate also drives canExecute itself — a real bound Button disables for all three
    /// directions the instant one navigation starts, not just the internal early-return above.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task All_three_commands_disable_while_one_navigation_is_in_flight() {
        var (backWhileBusy, skipWhileBusy, nextWhileBusy, backAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>();
            var connect = new FakeWizardStep(WizardStepId.Edition) { CanLeave = (_, _) => gate.Task };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            var skipTask = vm.SkipCommand.Execute().ToTask(); // starts, suspends on gate.Task

            var backWhileBusy = CanExecute(vm.BackCommand);
            var skipWhileBusy = CanExecute(vm.SkipCommand);
            var nextWhileBusy = CanExecute(vm.NextCommand);

            gate.SetResult(true);
            await skipTask;

            return (backWhileBusy, skipWhileBusy, nextWhileBusy, CanExecute(vm.BackCommand));
        });

        await Assert.That(backWhileBusy).IsFalse();
        await Assert.That(skipWhileBusy).IsFalse();
        await Assert.That(nextWhileBusy).IsFalse();
        await Assert.That(backAfter).IsTrue(); // idle again once the transition settles
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task CanLeaveAsync_throwing_is_treated_as_a_veto_and_does_not_crash() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition) { CanLeave = (_, _) => throw new InvalidOperationException("boom") };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // must not throw out of the command

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.Edition); // treated as a veto: stayed
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OnEnterAsync_throwing_still_completes_the_transition_and_does_not_crash() {
        var currentId = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { ThrowOnEnter = true };
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;

            await vm.NextCommand.Execute().ToTask(); // must not throw out of the command

            return vm.Current.Id;
        });

        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // transition still landed
    }

    // ── TryGoTo: the Sign-in step's retarget jump ────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_jumps_backwards_and_enters_the_target_step() {
        var (accepted, currentId, entered) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask(); // now on Sign-in

            var ok = vm.TryGoTo(WizardStepId.Edition);
            await WaitForIdleAsync(vm);

            return (ok, vm.Current.Id, connect.EnterCount);
        });

        await Assert.That(accepted).IsTrue();
        await Assert.That(currentId).IsEqualTo(WizardStepId.Edition);
        await Assert.That(entered).IsEqualTo(2); // the initial entry plus the jump
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_consults_the_current_steps_veto_like_any_transition() {
        var (accepted, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var signIn = new FakeWizardStep(WizardStepId.SignIn) { CanLeave = (_, _) => Task.FromResult(false) };
            var vm = new OnboardingViewModel([connect, signIn]);
            await vm.PendingEnterForTesting;
            await vm.NextCommand.Execute().ToTask(); // now on Sign-in, which refuses to leave

            var ok = vm.TryGoTo(WizardStepId.Edition);
            await WaitForIdleAsync(vm);

            return (ok, vm.Current.Id);
        });

        await Assert.That(accepted).IsTrue();  // the jump was admitted...
        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // ...and then vetoed by the step
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_is_refused_while_another_navigation_is_in_flight() {
        var (secondAttempt, currentId) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connect = new FakeWizardStep(WizardStepId.Edition) { CanLeave = (_, _) => gate.Task };
            var signIn = new FakeWizardStep(WizardStepId.SignIn);
            var done = new FakeWizardStep(WizardStepId.Done);
            var vm = new OnboardingViewModel([connect, signIn, done]);
            await vm.PendingEnterForTesting;

            var first = vm.NextCommand.Execute().ToTask(); // parks inside Connect's CanLeaveAsync
            var refused = vm.TryGoTo(WizardStepId.Done);

            gate.SetResult(true);
            await first;
            await WaitForIdleAsync(vm);

            return (refused, vm.Current.Id);
        }).WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.That(secondAttempt).IsFalse();
        await Assert.That(currentId).IsEqualTo(WizardStepId.SignIn); // only the first navigation landed
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task TryGoTo_refuses_a_step_that_is_not_part_of_this_run() {
        var accepted = await AvaloniaSession.DispatchAsync(async () => {
            var connect = new FakeWizardStep(WizardStepId.Edition);
            var shim = new FakeWizardStep(WizardStepId.Harnesses) { Applicable = false };
            var vm = new OnboardingViewModel([shim, connect]);
            await vm.PendingEnterForTesting;

            return vm.TryGoTo(WizardStepId.Harnesses);
        });

        await Assert.That(accepted).IsFalse();
    }

    // TryGoTo's navigation is fire-and-forget by design (it answers the caller immediately), so
    // tests wait for the shared gate to reopen rather than for a task they were never handed.
    static async Task WaitForIdleAsync(OnboardingViewModel vm) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!CanExecute(vm.NextCommand)) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the wizard never went idle");
            await Task.Delay(10);
        }
    }
}
