using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Update;
using Capacitor.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class UpdateFinishViewModelTests {
    static readonly AppUpdateTransition Update = new("op", AppUpdateTransition.InApp, "1.4.0", "1.4.1", DateTimeOffset.UnixEpoch);
    static readonly AttachStatus Connected = new(AttachState.Connected, null, []);
    static readonly AttachStatus Restarting = new(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null);
    static readonly AttachStatus Down = new(AttachState.Unreachable, "daemon_unreachable", null);

    [Test]
    [Arguments("1.4.1+abc", false, UpdateStepState.Done)]
    [Arguments("1.4.0", true, UpdateStepState.Note)]
    [Arguments("1.4.0", false, UpdateStepState.Running)]
    public async Task A_connected_daemon_is_done_only_at_the_new_version(string version, bool restartPending, UpdateStepState expected) {
        await Assert.That(UpdateFinishViewModel.DaemonStep(Connected, version, "connected", restartPending, "1.4.1").State).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Explicit_open_bypasses_predecessor_shutdown_even_after_automatic_completion(bool automaticStarted) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var clock = new FakeTimeProvider();
            using var vm = new UpdateFinishViewModel(Update, Observable.Return(Connected),
                Observable.Return<(string?, string?)>(("1.4.1", "connected")), Observable.Return(false),
                Observable.Return<LocalStepInput?>(null), clock);
            var earlier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = false;
            Task? automatic = null;
            vm.Completed += () => automatic = App.CompleteUpdateAsync(earlier.Task, () => opened = true);
            vm.OpenRequested += () => opened = true;
            if (automaticStarted) {
                clock.Advance(UpdateFinishViewModel.HandOverDelay);
                await WaitUntilAsync(() => automatic is not null);
            }
            try {
                await Assert.That(opened).IsFalse();
                vm.OpenCommand.Execute().Subscribe();
                await Assert.That(opened).IsTrue();
            } finally {
                earlier.SetResult();
                if (automatic is not null) await automatic;
            }
        });
    }

    [Test]
    public async Task An_expected_restart_is_progress_and_an_unexpected_outage_is_a_problem() {
        await Assert.That(UpdateFinishViewModel.DaemonStep(Restarting, null, null, true, "1.4.1").State).IsEqualTo(UpdateStepState.Running);
        await Assert.That(UpdateFinishViewModel.DaemonStep(Down, null, null, false, "1.4.1").State).IsEqualTo(UpdateStepState.Problem);
    }

    /// An old daemon cannot talk to the new app until it restarts into the new binary, so that is progress at first.
    [Test]
    public async Task An_incompatible_daemon_is_progress_until_the_patience_runs_out() {
        var incompatible = new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null);

        await Assert.That(UpdateFinishViewModel.DaemonStep(incompatible, null, null, false, "1.4.1").State).IsEqualTo(UpdateStepState.Running);
        await Assert.That(UpdateFinishViewModel.DaemonStep(incompatible, null, null, false, "1.4.1", waitedLong: true).State).IsEqualTo(UpdateStepState.Problem);
    }

    [Test]
    public async Task A_running_local_upgrade_keeps_its_percentage_apart_from_the_detail() {
        var downloading = UpdateFinishViewModel.LocalStep(new LocalStepInput(true, null, "Upgrading kcap local 1.4.0 to 1.4.1",
            "Downloading 1.4.1… 71%", Phase: LocalUpgradePhase.Downloading, From: "1.4.0", To: "1.4.1", DownloadFraction: 0.71))!;
        var starting = UpdateFinishViewModel.LocalStep(new LocalStepInput(true, null, null, null,
            Phase: LocalUpgradePhase.Starting, From: "1.4.0", To: "1.4.1"))!;

        await Assert.That(downloading.State).IsEqualTo(UpdateStepState.Running);
        await Assert.That(downloading.Title).IsEqualTo("Local server 1.4.0 → 1.4.1");
        await Assert.That(downloading.Detail).IsEqualTo("Downloading…");
        await Assert.That(downloading.Trailing).IsEqualTo("71%");
        await Assert.That(downloading.Hint).IsEqualTo("Upgrading kcap local 1.4.0 to 1.4.1. Downloading 1.4.1… 71%");
        await Assert.That(starting.Detail).IsEqualTo("Starting… A migration can take a few minutes");
    }

    [Test]
    public async Task The_local_server_step_settles_to_its_outcome() {
        var upgraded = UpdateFinishViewModel.LocalStep(new LocalStepInput(false, new LocalUpgradeOutcome.Upgraded("1.4.0", "1.4.1"), null, null))!;
        var notPublished = UpdateFinishViewModel.LocalStep(new LocalStepInput(false, new LocalUpgradeOutcome.NotPublished("no bundle"),
            null, null, From: "1.4.0", To: "1.4.1"))!;
        var failed = UpdateFinishViewModel.LocalStep(new LocalStepInput(false,
            new LocalUpgradeOutcome.Failed("start_failed", "kcap local did not start.", true, LocalInstallAffordance.For(null), "1.4.0", "1.4.1"),
            null, null))!;
        var notSwitched = UpdateFinishViewModel.LocalStep(new LocalStepInput(false,
            new LocalUpgradeOutcome.Failed("download_failed", "The download failed. Run kcap local upgrade to retry.", false,
                LocalInstallAffordance.For("download_failed"), "1.4.0", "1.4.1"),
            null, null))!;
        var notStarted = UpdateFinishViewModel.LocalStep(new LocalStepInput(false, null, null, null))!;

        await Assert.That(upgraded is { Title: "Local server 1.4.1", Detail: "Upgraded", State: UpdateStepState.Done }).IsTrue();
        await Assert.That(notPublished.State).IsEqualTo(UpdateStepState.Note);
        await Assert.That(notPublished.Detail).IsEqualTo("1.4.1 is not available yet. Staying on 1.4.0");
        await Assert.That(failed.State).IsEqualTo(UpdateStepState.Problem);
        await Assert.That(failed.Detail).IsEqualTo("1.4.1 did not start. Open kcap and press Start");
        await Assert.That(notSwitched.Detail).IsEqualTo("The download failed. Staying on 1.4.0");
        await Assert.That(notStarted.State).IsEqualTo(UpdateStepState.Pending);
        await Assert.That(UpdateFinishViewModel.LocalStep(new LocalStepInput(false, null, null, null, Skipped: true))).IsNull();
        await Assert.That(UpdateFinishViewModel.LocalStep(null)).IsNull();
    }

    /// No step detail closes with a full stop, and none splits two clauses with a semicolon.
    [Test]
    public async Task Step_details_are_fragments() {
        var details = new[] {
            UpdateFinishViewModel.DaemonStep(Connected, "1.4.0", "connected", true, "1.4.1"),
            UpdateFinishViewModel.DaemonStep(Connected, "1.4.0", "connected", false, "1.4.1", waitedLong: true),
            UpdateFinishViewModel.DaemonStep(Connected, "1.5.0", "connected", false, "1.4.1"),
            UpdateFinishViewModel.DaemonStep(Restarting, null, null, true, "1.4.1"),
            UpdateFinishViewModel.DaemonStep(Down, null, null, false, "1.4.1"),
            UpdateFinishViewModel.DaemonStep(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null), null, null, false, "1.4.1", waitedLong: true),
            UpdateFinishViewModel.DaemonStep(Connected, "1.4.1", "reconnecting", false, "1.4.1"),
            UpdateFinishViewModel.DaemonStep(Connected, "1.4.1", "disconnected", false, "1.4.1", waitedLong: true),
        }.Concat(new UpdateFinishViewModel("1.4.0", "1.4.1", new FakeTimeProvider(), stopsDaemon: true).Steps)
            .Select(step => step.Detail!).ToList();

        foreach (var detail in details) {
            await Assert.That(detail.EndsWith('.')).IsFalse();
            await Assert.That(detail).DoesNotContain(";");
        }
    }

    [Test]
    public async Task Everything_done_settles_clean_and_a_note_settles_without_being_clean() {
        var done = UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "connected", false, new LocalStepInput(false, new LocalUpgradeOutcome.NothingToDo(null), null, null));
        var noted = UpdateFinishViewModel.Project(Update, Connected, "1.4.0", "connected", true, null);
        var busy = UpdateFinishViewModel.Project(Update, Restarting, null, null, true, null);

        await Assert.That(done is { Settled: true, Clean: true }).IsTrue();
        await Assert.That(done.Steps.Select(step => step.Title).SequenceEqual(["kcap 1.4.1", "Local server", "Daemon 1.4.1"])).IsTrue();
        await Assert.That(done.Steps.Select(step => step.Detail).SequenceEqual(["Installed", "Up to date", "Running"])).IsTrue();
        await Assert.That(noted is { Settled: true, Clean: false }).IsTrue();
        await Assert.That(busy.Settled).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_clean_finish_hands_over_by_itself_and_a_problem_waits_for_the_user() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var clock = new FakeTimeProvider();
            var status = new BehaviorSubject<AttachStatus>(Restarting);
            var daemon = new BehaviorSubject<(string?, string?)>((null, null));
            using var vm = new UpdateFinishViewModel(Update, status, daemon, Observable.Return(true), Observable.Return<LocalStepInput?>(null), clock);
            var completed = 0;
            vm.Completed += () => completed++;

            await Assert.That(vm.Heading).IsEqualTo("Finishing the update…");
            await Assert.That(vm.OpenLabel).IsEqualTo("Open kcap");
            status.OnNext(Connected);
            daemon.OnNext(("1.4.1", "connected"));
            await Assert.That(vm.Heading).IsEqualTo("kcap 1.4.1 is ready");
            clock.Advance(UpdateFinishViewModel.HandOverDelay);
            await WaitUntilAsync(() => completed == 1);

            using var stuck = new UpdateFinishViewModel(Update, Observable.Return(Down), Observable.Return<(string?, string?)>((null, null)),
                Observable.Return(false), Observable.Return<LocalStepInput?>(null), clock);
            var stuckCompleted = 0;
            stuck.Completed += () => stuckCompleted++;
            clock.Advance(TimeSpan.FromMinutes(1));
            await Assert.That(stuck.Heading).IsEqualTo("kcap 1.4.1 is installed");
            await Assert.That(stuckCompleted).IsEqualTo(0);
            stuck.OpenCommand.Execute().Subscribe();
            await Assert.That(stuckCompleted).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_steps_show_before_the_daemon_first_reports() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var vm = new UpdateFinishViewModel(Update, Observable.Never<AttachStatus>(), Observable.Never<(string?, string?)>(),
                Observable.Never<bool>(), Observable.Never<LocalStepInput?>(), new FakeTimeProvider());

            await Assert.That(vm.Steps.Select(step => step.Title).SequenceEqual(["kcap 1.4.1", "Daemon"])).IsTrue();
            await Assert.That(vm.Steps[1].Detail).IsEqualTo("Connecting…");
        });
    }

    [Test]
    [Arguments(false, "Closing kcap", "The daemon and its agents keep running")]
    [Arguments(true, "Closing kcap and the daemon", "Agents resume after the update")]
    public async Task The_closing_window_says_what_happens_to_agents_and_offers_nothing_to_open(bool stopsDaemon, string title, string agents) {
        using var vm = new UpdateFinishViewModel("1.4.0", "1.4.1", new FakeTimeProvider(), stopsDaemon);

        await Assert.That(vm.Heading).IsEqualTo("Updating to kcap 1.4.1…");
        await Assert.That(vm.CanOpen).IsFalse();
        await Assert.That(vm.Steps.Select(step => step.State).SequenceEqual([UpdateStepState.Running, UpdateStepState.Pending])).IsTrue();
        await Assert.That(vm.Steps[0].Title).IsEqualTo(title);
        await Assert.That(vm.Steps[0].Detail).IsEqualTo(agents);
    }

    /// The main window reads a daemon off the server as an outage, so the update is not finished before it reconnects.
    [Test]
    public async Task A_daemon_on_the_new_version_is_done_only_once_it_is_back_on_the_server() {
        var redialing = UpdateFinishViewModel.DaemonStep(Connected, "1.4.1", "disconnected", false, "1.4.1");
        var gaveUp = UpdateFinishViewModel.DaemonStep(Connected, "1.4.1", "disconnected", false, "1.4.1", waitedLong: true);

        await Assert.That(redialing is { Title: "Daemon 1.4.1", Detail: "Connecting to the server…", State: UpdateStepState.Running }).IsTrue();
        await Assert.That(gaveUp is { Detail: "Not connected to the server yet", State: UpdateStepState.Note }).IsTrue();
        await Assert.That(UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "reconnecting", false, null).Settled).IsFalse();
    }

    /// The daemon cannot reach a local server that is being upgraded, so its patience starts over once the upgrade settles.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_daemon_patience_starts_over_when_the_local_upgrade_settles() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var clock = new FakeTimeProvider();
            var local = new BehaviorSubject<LocalStepInput?>(new LocalStepInput(true, null, null, null, Phase: LocalUpgradePhase.Starting));
            using var vm = new UpdateFinishViewModel(Update, Observable.Return(Connected),
                Observable.Return<(string?, string?)>(("1.4.1", "disconnected")), Observable.Return(false), local, clock);

            clock.Advance(UpdateFinishViewModel.DaemonPatience * 2);
            local.OnNext(new LocalStepInput(false, new LocalUpgradeOutcome.Upgraded("1.4.0", "1.4.1"), null, null));
            await Assert.That(vm.Steps[^1].State).IsEqualTo(UpdateStepState.Running);

            clock.Advance(UpdateFinishViewModel.DaemonPatience);
            await WaitUntilAsync(() => vm.Steps[^1].State == UpdateStepState.Note);
        });
    }

    /// Going on early hands over the window but not the update: its progress keeps following the steps.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Progress_keeps_following_the_steps_after_an_early_hand_over() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var status = new BehaviorSubject<AttachStatus>(Restarting);
            var daemon = new BehaviorSubject<(string?, string?)>((null, null));
            using var vm = new UpdateFinishViewModel(Update, status, daemon, Observable.Return(false), Observable.Return<LocalStepInput?>(null), new FakeTimeProvider());
            var progress = new List<(bool Finishing, string? Detail)>();
            using var _ = vm.Progress.Subscribe(progress.Add);

            vm.OpenCommand.Execute().Subscribe();
            await Assert.That(progress[^1]).IsEqualTo((true, "Restarting the daemon…"));

            status.OnNext(Connected);
            daemon.OnNext(("1.4.1", "connected"));
            await Assert.That(progress[^1]).IsEqualTo((false, (string?)null));
        });
    }

    /// A daemon already on the new version waits for the local server, so it never shows done and then drops back when the server stops.
    [Test]
    public async Task A_daemon_on_the_new_version_waits_for_the_local_server_upgrade() {
        var upgrading = new LocalStepInput(true, null, null, null, Phase: LocalUpgradePhase.Downloading, From: "1.4.0", To: "1.4.1");
        var during = UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "connected", false, upgrading);
        var after = UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "connected", false,
            new LocalStepInput(false, new LocalUpgradeOutcome.Upgraded("1.4.0", "1.4.1"), null, null));

        await Assert.That(during.Steps[^1] is { Detail: "Waiting for the local server…", State: UpdateStepState.Running }).IsTrue();
        await Assert.That(after.Steps[^1].State).IsEqualTo(UpdateStepState.Done);
    }

    /// The launcher names what is happening in plain words, without the versions the window's titles carry.
    [Test]
    public async Task The_launcher_reads_what_the_first_running_step_is_doing() {
        var downloading = UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "connected", false,
            new LocalStepInput(true, null, null, null, Phase: LocalUpgradePhase.Downloading, From: "1.4.0", To: "1.4.1"));
        var reconnecting = UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "disconnected", false, null);

        await Assert.That(UpdateFinishViewModel.ProgressDetail(downloading.Steps)).IsEqualTo("Downloading the local server…");
        await Assert.That(UpdateFinishViewModel.ProgressDetail(reconnecting.Steps)).IsEqualTo("Reconnecting the daemon to the server…");
        await Assert.That(UpdateFinishViewModel.ProgressDetail(UpdateFinishViewModel.Project(Update, Connected, "1.4.1", "connected", false, null).Steps)).IsNull();
    }

    static async Task WaitUntilAsync(Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task A_daemon_that_does_not_move_settles_as_a_note_after_a_while_and_a_newer_one_at_once() {
        var waiting = UpdateFinishViewModel.DaemonStep(Connected, "1.4.0", "connected", false, "1.4.1");
        var waited = UpdateFinishViewModel.DaemonStep(Connected, "1.4.0", "connected", false, "1.4.1", waitedLong: true);
        var newer = UpdateFinishViewModel.DaemonStep(Connected, "1.5.0", "connected", false, "1.4.1");

        await Assert.That(waiting.State).IsEqualTo(UpdateStepState.Running);
        await Assert.That(waiting.Detail).IsEqualTo("Picking up 1.4.1…");
        await Assert.That(UpdateFinishViewModel.DaemonStep(Restarting, null, null, false, "1.4.1").Detail).IsEqualTo("Restarting into 1.4.1…");
        await Assert.That(waited.State).IsEqualTo(UpdateStepState.Note);
        await Assert.That(waited.Detail).IsEqualTo("Still on 1.4.0. Moves to 1.4.1 on its next restart");
        await Assert.That(newer.State).IsEqualTo(UpdateStepState.Note);
    }
}
