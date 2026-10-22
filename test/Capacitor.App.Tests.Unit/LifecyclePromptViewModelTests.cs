using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// LifecyclePromptViewModel has no activation-scoped state (no queue, no ticker — unlike
/// ConsentPromptViewModel, which this suite is modeled on) so these run under
/// AvaloniaSession.WithImmediateRxScheduler — TrayViewModelTests/MainWindowViewModelTests' own
/// pattern for a plain, non-activatable VM — rather than the real headless dispatcher
/// ConsentPromptViewModelTests needs for its Activator/ticker plumbing.
public class LifecyclePromptViewModelTests {
    static LifecyclePrompt Prompt(string kind, bool pathDegraded = false, string disclosure = "the unit will be replaced.") =>
        new(kind, "1.0.0", "1.1.0", pathDegraded, disclosure);

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(LifecyclePrompt.KindTakeover, "Take over daemon management")]
    [Arguments(LifecyclePrompt.KindRepair, "Repair daemon service")]
    [Arguments(LifecyclePrompt.KindQuarantine, "Corrupted consent claims file")]
    public async Task Title_is_kind_specific(string kind, string expectedTitle) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(kind), new TaskCompletionSource<bool>());

            await Assert.That(vm.Title).IsEqualTo(expectedTitle);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Quarantine_kind_renders_confirm_only_with_an_acknowledge_button() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindQuarantine), new TaskCompletionSource<bool>());

            await Assert.That(vm.ShowDeclineButton).IsFalse();
            await Assert.That(vm.AcceptButtonText).IsEqualTo("Acknowledge");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Non_quarantine_kinds_render_both_buttons() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), new TaskCompletionSource<bool>());

            await Assert.That(vm.ShowDeclineButton).IsTrue();
            await Assert.That(vm.AcceptButtonText).IsEqualTo("Continue");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Disclosure_is_always_present() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(
                Prompt(LifecyclePrompt.KindRepair, disclosure: "the existing unit will be replaced and its settings re-captured."),
                new TaskCompletionSource<bool>());

            await Assert.That(vm.Disclosure).IsEqualTo("the existing unit will be replaced and its settings re-captured.");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PathDegraded_false_renders_no_degraded_path_sentence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair, pathDegraded: false), new TaskCompletionSource<bool>());

            await Assert.That(vm.PathDegraded).IsFalse();
            await Assert.That(vm.DegradedPathText).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PathDegraded_true_adds_the_degraded_path_sentence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindTakeover, pathDegraded: true), new TaskCompletionSource<bool>());

            await Assert.That(vm.PathDegraded).IsTrue();
            await Assert.That(vm.DegradedPathText).IsNotNull();
            await Assert.That(vm.DegradedPathText!).Contains("PATH");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accept_resolves_the_task_true_and_requests_close() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);
            var closeRequests = 0;
            using var sub = vm.CloseRequested.Subscribe(_ => closeRequests++);

            await vm.AcceptCommand.Execute().ToTask();

            await Assert.That(tcs.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(tcs.Task.Result).IsTrue();
            await Assert.That(closeRequests).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Decline_resolves_the_task_false_and_requests_close() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);
            var closeRequests = 0;
            using var sub = vm.CloseRequested.Subscribe(_ => closeRequests++);

            await vm.DeclineCommand.Execute().ToTask();

            await Assert.That(tcs.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(tcs.Task.Result).IsFalse();
            await Assert.That(closeRequests).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accept_after_external_cancellation_is_a_silent_no_op() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var tcs = new TaskCompletionSource<bool>();
            tcs.TrySetResult(false); // simulates WireDialogCancellation already having resolved it
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindRepair), tcs);

            await vm.AcceptCommand.Execute().ToTask();

            await Assert.That(tcs.Task.Result).IsFalse(); // TrySetResult on Accept is a no-op
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Update_ready_offers_restart_now_and_not_now() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(
                new LifecyclePrompt(LifecyclePrompt.KindUpdateReady, "0.12.0-beta.3", "0.12.0-beta.2", false, "ready"), new TaskCompletionSource<bool>());

            await Assert.That(vm.Title).IsEqualTo("Update ready");
            await Assert.That(vm.AcceptButtonText).IsEqualTo("Restart now");
            await Assert.That(vm.ShowDeclineButton).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Update_info_is_acknowledge_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(
                new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, "0.12.0-beta.2", false, "up to date"), new TaskCompletionSource<bool>());

            await Assert.That(vm.Title).IsEqualTo("Software update");
            await Assert.That(vm.AcceptButtonText).IsEqualTo("Dismiss");
            await Assert.That(vm.ShowDeclineButton).IsFalse();
        });
    }

    [Test]
    public async Task Reload_service_prompt_has_its_title_and_accept_label() {
        var vm = new LifecyclePromptViewModel(
            new LifecyclePrompt(LifecyclePrompt.KindReloadService, null, null, false, "disclosure"), new TaskCompletionSource<bool>());
        await Assert.That(vm.Title).IsEqualTo("Reload the daemon service");
        await Assert.That(vm.AcceptButtonText).IsEqualTo("Reload now");
        await Assert.That(vm.ShowDeclineButton).IsTrue();
    }

    /// Prompt() passes DaemonVersion "1.0.0" and CliVersion "1.1.0"; for update-ready those are
    /// the candidate and the installed build.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Update_ready_shows_installed_to_candidate_versions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindUpdateReady), new TaskCompletionSource<bool>());

            await Assert.That(vm.ShowVersionChange).IsTrue();
            await Assert.That(vm.InstalledVersion).IsEqualTo("1.1.0");
            await Assert.That(vm.UpdateVersion).IsEqualTo("1.0.0");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Other_kinds_show_no_version_change_even_with_versions_set() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new LifecyclePromptViewModel(Prompt(LifecyclePrompt.KindTakeover), new TaskCompletionSource<bool>());

            await Assert.That(vm.ShowVersionChange).IsFalse();
            await Assert.That(vm.InstalledVersion).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Update_ready_without_an_installed_version_still_shows_the_candidate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindUpdateReady, "1.0.0", null, false, "restart to finish.");
            var vm = new LifecyclePromptViewModel(prompt, new TaskCompletionSource<bool>());

            await Assert.That(vm.ShowVersionChange).IsTrue();
            await Assert.That(vm.UpdateVersion).IsEqualTo("1.0.0");
            await Assert.That(vm.ShowInstalledVersion).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Uninstall_is_destructive() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindUninstall, null, null, false, "removes kcap.", "data is deleted");
            var vm = new LifecyclePromptViewModel(prompt, new TaskCompletionSource<bool>());

            await Assert.That(vm.Title).IsEqualTo("Uninstall kcap");
            await Assert.That(vm.AcceptButtonText).IsEqualTo("Uninstall");
            await Assert.That(vm.AcceptIsDestructive).IsTrue();
            await Assert.That(vm.Warning).IsEqualTo("data is deleted");
        });
    }
}
