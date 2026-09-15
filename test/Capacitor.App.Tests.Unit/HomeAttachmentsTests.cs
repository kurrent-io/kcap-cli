using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The launcher's attachment path: the gate, the captured draft an upload is built from, and the
/// draft retained until the daemon confirms the launch. Every test runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")] — the
/// same process-global scheduler HomeViewModelTests pins.
public class HomeAttachmentsTests {
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";
    const string CapableVersion = "1.0.3";
    const string StaleVersion = "1.0.2";

    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, LaunchedId, null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            return Task.FromResult(Next);
        }
    }

    /// Pushes a LaunchFailure for the accepted id before the invoke returns, so the failure is
    /// buffered before StartAsync ever registers the launch.
    sealed class FailureBeforeReturnLaunchClient : ILaunchClient {
        public required Subject<LaunchFailure> Failures { get; init; }
        public required string Reason { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Failures.OnNext(new LaunchFailure(LaunchedId, Reason));
            return Task.FromResult(new LaunchOutcome(true, LaunchedId, null));
        }
    }

    /// Adds the directory row for the accepted id before the invoke returns — the launch is
    /// already confirmed by the time StartAsync records it.
    sealed class RowBeforeReturnLaunchClient : ILaunchClient {
        public required FakeAgentDirectory Directory { get; init; }

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Directory.Rows.AddOrUpdate(Row(LaunchedId, request.RepoPath));
            return Task.FromResult(new LaunchOutcome(true, LaunchedId, null));
        }
    }

    static AgentRow Row(string id, string? repoPath) =>
        AgentRow.FromLocal(
            new AgentStatusDto(id, "agent", "claude", repoPath, "Running",
                FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
                RequesterDisplay: null),
            new RepoIdentity($"path:{repoPath}", "a"));

    /// Everything a launcher needs to attach: a connected daemon advertising input/2, a connected
    /// lane, a viewer id and a fake clock the retention window is measured on.
    sealed class Rig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public RecordingLaunchClient Launch { get; } = new();
        public ScriptedUploader Uploader { get; } = new();
        public Subject<LaunchFailure> Failures { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public BehaviorSubject<ServerLaneStatus> Lane { get; } = new(new ServerLaneStatus(ServerLaneState.Connected));
        public BehaviorSubject<IReadOnlyList<DaemonInfo>> Daemons { get; } = new([]);
        public FakeAgentDirectory Directory { get; } = new();
        public string? ViewerId { get; set; } = "viewer-1";
        public HomeViewModel Vm { get; private set; } = null!;

        public HomeViewModel Start(string statePath, IReadOnlyList<string>? capabilities = null, ILaunchClient? launch = null) {
            Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected"));
            Daemon.StatusSubject.OnNext(new AttachStatus(
                AttachState.Connected, null, capabilities ?? ["input/1", "input/2"]));
            Vm = new HomeViewModel(
                Daemon, new AppStateStore(statePath), launch ?? Launch, () => Task.FromResult<string[]>([]),
                daemons: Daemons, viewerId: _ => Task.FromResult(ViewerId), laneStatus: Lane,
                launchFailures: Failures, directory: Directory, uploader: Uploader, time: Time);
            return Vm;
        }

        public void Dispose() {
            Vm?.Dispose();
            Directory.Dispose();
            Failures.Dispose();
            Lane.Dispose();
            Daemons.Dispose();
        }
    }

    static DaemonInfo Machine(string name, string? version) => new() {
        Name = name, OwnerUserId = "viewer-1", Connected = true, Version = version,
        RepoPaths = ["/remote/repo"], SupportedVendors = ["claude"],
    };

    static StagedAttachment Chip(string name) => new(name, "image/png", new byte[] { 1, 2, 3 });

    /// Stages one chip and a goal, then runs an accepted launch. `duringUpload` runs while the
    /// upload is still in flight — the window a draft has to be edited out from under.
    static async Task<StagedAttachment> LaunchWithDraftAsync(
            Rig rig, string statePath, Func<HomeViewModel, Task>? duringUpload = null) {
        var vm = rig.Start(statePath);
        await vm.SelectRepositoryAsync("/repo/a");
        var chip = Chip("a.png");
        vm.Attachments.Accept(new IntakeResult([chip], []));
        vm.Goal = "g";
        if (duringUpload is null) {
            await vm.StartCommand.Execute();
            return chip;
        }

        var gate = new TaskCompletionSource<UploadOutcome>();
        rig.Uploader.Pending = gate;
        var run = vm.StartCommand.Execute().ToTask();
        await duringUpload(vm);
        gate.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1"], null));
        await run;
        return chip;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Can_attach_follows_sign_in_input_2_locally_and_the_version_gate_remotely() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path, capabilities: ["input/1"]);

            await Assert.That(vm.CanAttach).IsFalse();
            await Assert.That(vm.AttachHint).IsEqualTo("attachments need the daemon updated");

            // A chip staged against a machine that cannot take it blocks Start rather than
            // launching without the files.
            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2"]));
            await Assert.That(vm.CanAttach).IsTrue();
            await Assert.That(vm.AttachHint).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            await Assert.That(vm.CanAttach).IsFalse();
            await Assert.That(vm.AttachHint).IsEqualTo("sign in to attach files");
            rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

            // A remote machine is gated on its own advertised version — the local capability list
            // says nothing about what another daemon accepts.
            rig.Daemons.OnNext([Machine("box", StaleVersion)]);
            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.CanAttach).IsFalse();
            await Assert.That(vm.AttachHint).IsEqualTo("attachments need the daemon updated");

            rig.Daemons.OnNext([Machine("box", CapableVersion)]);
            await Assert.That(vm.CanAttach).IsTrue();
            await Assert.That(vm.AttachHint).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Launch_is_built_from_the_captured_draft_and_the_gate_reruns_against_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
            vm.Goal = "g";

            var gate = new TaskCompletionSource<UploadOutcome>();
            rig.Uploader.Pending = gate;
            var run = vm.StartCommand.Execute().ToTask();
            await Assert.That(vm.Uploading).IsTrue();

            await vm.SelectRepositoryAsync("/repo/other");
            await vm.ChooseHarnessAsync("codex");
            gate.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1"], null));
            await run;

            await Assert.That(rig.Launch.Last!.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(rig.Launch.Last!.Vendor).IsEqualTo(HomeViewModel.DefaultVendor);
            await Assert.That(rig.Launch.Last!.Prompt).IsEqualTo("g");
            await Assert.That(rig.Launch.Last!.AttachmentIds).IsEquivalentTo(new[] { "u1" });
            await Assert.That(vm.Uploading).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_goal_with_files_launches() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));

            await vm.StartCommand.Execute();

            await Assert.That(rig.Launch.Last!.Prompt).IsEqualTo("");
            await Assert.That(rig.Launch.Last!.AttachmentIds).IsEquivalentTo(new[] { "u1" });
            await Assert.That(vm.Tray.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Upload_failure_sets_start_error_and_launches_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
            vm.Goal = "g";
            rig.Uploader.Next = UploadOutcome.Rejected("that file type isn't allowed");

            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo("that file type isn't allowed");
            await Assert.That(rig.Launch.Last).IsNull();
            await Assert.That(vm.Tray.Count).IsEqualTo(1);
            await Assert.That(vm.Goal).IsEqualTo("g");

            // A 401 is the sign-in case, never raw transport text.
            rig.Uploader.Next = UploadOutcome.Unauthorized("401");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo("sign in to attach files");
            await Assert.That(rig.Launch.Last).IsNull();
            await Assert.That(vm.SignInVisible).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Started_clears_only_what_was_sent() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            await vm.SelectRepositoryAsync("/repo/a");
            var sent = Chip("a.png");
            var dropped = Chip("c.png");
            vm.Attachments.Accept(new IntakeResult([sent, dropped], []));
            vm.Goal = "g";

            var gate = new TaskCompletionSource<UploadOutcome>();
            rig.Uploader.Pending = gate;
            var run = vm.StartCommand.Execute().ToTask();
            vm.Goal = "edited while sending";
            vm.Attachments.Accept(new IntakeResult([Chip("b.png")], []));
            vm.Tray.Remove(dropped);
            gate.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1", "u2"], null));
            await run;

            await Assert.That(vm.Goal).IsEqualTo("edited while sending");
            await Assert.That(vm.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(new[] { "b.png" });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unusable_id_retains_no_draft_and_says_so() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            rig.Launch.Next = new LaunchOutcome(true, "   ", null);
            await vm.SelectRepositoryAsync("/repo/a");
            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
            vm.Goal = "g";

            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError)
                .IsEqualTo(HomeViewModel.UnusableIdMessage + " — re-attach the files if it did not start");
            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.Tray.Count).IsEqualTo(0);

            // Nothing was retained, so no later failure can bring the draft back.
            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));
            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.Tray.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Delayed_failure_restores_a_draft_this_launch_emptied_with_the_real_reason() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var chip = await LaunchWithDraftAsync(rig, path);
            var vm = rig.Vm;

            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.Tray.Count).IsEqualTo(0);

            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

            await Assert.That(vm.StartError).IsEqualTo("boom — your draft is back");
            await Assert.That(vm.Goal).IsEqualTo("g");
            await Assert.That(vm.Tray.Items.Single().Id).IsEqualTo(chip.Id);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Delayed_failure_does_not_restore_over_user_edits_or_a_changed_target() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = new TempDir();
            const string ReAttach = "boom — re-attach the files to send them again";

            // (a) the user typed a new goal after the launch was accepted
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("a.json"));
                rig.Vm.Goal = "new";
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(rig.Vm.StartError).IsEqualTo(ReAttach);
                await Assert.That(rig.Vm.Goal).IsEqualTo("new");
                await Assert.That(rig.Vm.Tray.Count).IsEqualTo(0);
            }

            // (b) the user blanked the goal themselves while the request was in flight
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("b.json"), vm => { vm.Goal = ""; return Task.CompletedTask; });
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(rig.Vm.StartError).IsEqualTo(ReAttach);
                await Assert.That(rig.Vm.Goal).IsEqualTo("");
            }

            // (c) a chip added and removed again during the request — the tray moved under the draft
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("c.json"), vm => {
                    var extra = Chip("b.png");
                    vm.Attachments.Accept(new IntakeResult([extra], []));
                    vm.Tray.Remove(extra);
                    return Task.CompletedTask;
                });
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(rig.Vm.StartError).IsEqualTo(ReAttach);
                await Assert.That(rig.Vm.Tray.Count).IsEqualTo(0);
            }

            // (d) a chip added during the request is still staged at failure time
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("d.json"), vm => {
                    vm.Attachments.Accept(new IntakeResult([Chip("b.png")], []));
                    return Task.CompletedTask;
                });
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(rig.Vm.StartError).IsEqualTo(ReAttach);
                await Assert.That(rig.Vm.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(new[] { "b.png" });
            }

            // (e) the launcher is pointed at a different repository by the time the failure lands
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("e.json"));
                await rig.Vm.SelectRepositoryAsync("/repo/elsewhere");
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(rig.Vm.StartError).IsEqualTo(ReAttach);
                await Assert.That(rig.Vm.Goal).IsEqualTo("");
                await Assert.That(rig.Vm.Tray.Count).IsEqualTo(0);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Second_accepted_attachment_launch_replaces_the_retained_bytes_but_the_first_still_reads_re_attach() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);
            await vm.SelectRepositoryAsync("/repo/a");

            vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
            vm.Goal = "first";
            await vm.StartCommand.Execute();

            vm.Attachments.Accept(new IntakeResult([Chip("b.png")], []));
            vm.Goal = "second";
            rig.Launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            await vm.StartCommand.Execute();

            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));
            await Assert.That(vm.StartError).IsEqualTo("boom — re-attach the files to send them again");
            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.Tray.Count).IsEqualTo(0);

            // The second launch's own bytes are untouched by the first launch's failure.
            rig.Failures.OnNext(new LaunchFailure(SecondLaunchedId, "nope"));
            await Assert.That(vm.StartError).IsEqualTo("nope — your draft is back");
            await Assert.That(vm.Goal).IsEqualTo("second");
            await Assert.That(vm.Tray.Items.Single().FileName).IsEqualTo("b.png");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retention_expires_ten_minutes_after_upload_on_the_clock_and_a_late_failure_is_untracked() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            await LaunchWithDraftAsync(rig, path);
            var vm = rig.Vm;

            rig.Time.Advance(TimeSpan.FromMinutes(11));
            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.Goal).IsEqualTo("");
            await Assert.That(vm.Tray.Count).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Row_confirmation_and_buffered_failure_before_registration_settle_the_draft() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = new TempDir();

            // A row that predates registration is success: the retained bytes are released, and a
            // later failure for the same id restores nothing.
            using (var rig = new Rig()) {
                var vm = rig.Start(tmp.PathTo("row.json"), launch: new RowBeforeReturnLaunchClient { Directory = rig.Directory });
                await vm.SelectRepositoryAsync("/repo/a");
                vm.Attachments.Accept(new IntakeResult([Chip("a.png")], []));
                vm.Goal = "g";
                await vm.StartCommand.Execute();

                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "boom"));

                await Assert.That(vm.StartError).IsNull();
                await Assert.That(vm.Goal).IsEqualTo("");
                await Assert.That(vm.Tray.Count).IsEqualTo(0);
            }

            // A failure buffered before registration settles the same launch the delayed one does.
            using (var rig = new Rig()) {
                var vm = rig.Start(tmp.PathTo("buffered.json"), launch: new FailureBeforeReturnLaunchClient {
                    Failures = rig.Failures, Reason = "boom",
                });
                await vm.SelectRepositoryAsync("/repo/a");
                var chip = Chip("a.png");
                vm.Attachments.Accept(new IntakeResult([chip], []));
                vm.Goal = "g";
                await vm.StartCommand.Execute();

                await Assert.That(vm.StartError).IsEqualTo("boom — your draft is back");
                await Assert.That(vm.Goal).IsEqualTo("g");
                await Assert.That(vm.Tray.Items.Single().Id).IsEqualTo(chip.Id);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Non_attachment_failure_reason_with_files_keeps_the_real_reason() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = new TempDir();

            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("denied.json"));
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "launch_denied_by_owner: default"));

                await Assert.That(rig.Vm.StartError).Contains("consent policy denied");
                await Assert.That(rig.Vm.StartError).EndsWith(" — your draft is back");
            }

            // Only the delivery-side reason gets attachment wording.
            using (var rig = new Rig()) {
                await LaunchWithDraftAsync(rig, tmp.PathTo("unavailable.json"));
                rig.Failures.OnNext(new LaunchFailure(LaunchedId, "attachment_unavailable: 3 of 3 gone"));

                await Assert.That(rig.Vm.StartError)
                    .IsEqualTo("the attached files could not be delivered to the machine — your draft is back");
            }
        });
    }

    /// Refusals are one line: named chips with their own reason, and the per-message cap grouped
    /// into a single entry rather than repeated per file.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refused_files_render_one_notice_with_the_cap_grouped() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);

            vm.Attachments.Accept(new IntakeResult([], [
                new IntakeRefusal("a.png", "is over 10 MB"),
                new IntakeRefusal("c.png", $"only {InputWire.MaxAttachmentsPerPrompt} files per message"),
                new IntakeRefusal("d.png", $"only {InputWire.MaxAttachmentsPerPrompt} files per message"),
            ]));

            await Assert.That(vm.StartError).IsEqualTo(
                "`a.png` is over 10 MB; only 10 files per message — `c.png`, `d.png` not added");

            // The next goal edit is the user moving on — the notice goes with it.
            vm.Goal = "g";
            await Assert.That(vm.StartError).IsNull();
        });
    }

    /// A handler-level failure names no file, so the reason stands on its own rather than being
    /// backticked behind a placeholder name.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refusal_that_names_no_file_renders_its_reason_alone() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var rig = new Rig();
            var vm = rig.Start(path);

            vm.Attachments.Accept(new IntakeResult([], [new IntakeRefusal("clipboard", "the clipboard could not be read")]));

            await Assert.That(vm.StartError).IsEqualTo("the clipboard could not be read");
        });
    }
}
