using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Startup phase, reconciliation, and the startup matrix. Every
/// clock-dependent wait goes through FakeTimeProvider (never Task.Delay-based ordering);
/// settling between an event push and its effect is driven by WaitUntilAsync polling on the
/// fakes' call counters (PauseControllerTests/ConsentServiceTests idiom).
///
/// Every mutating branch routes through a fake lane (FakeMutationLane), never IKcapCli's
/// mutation methods: FakeKcapCli's StartVerified/InstallVerified/DetachedStart call counts must
/// stay 0 everywhere, alongside the Lane.Requests assertions.
public class DaemonLifecycleControllerTests {
    static readonly TimeSpan TxnActiveRequeryDelay = DaemonLifecycleController.TxnActiveRequeryDelay;

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static ServiceSnapshot Snap(
            bool unitPresent = false, string state = "not_installed", string? installBinaryPath = "/opt/kcap/kcapd",
            string? binaryPath = null, int? jobPid = null, int? daemonPid = null, bool txnMarker = false,
            bool txnActive = false) =>
        new("default", unitPresent, state, binaryPath, installBinaryPath, jobPid, daemonPid, txnMarker, txnActive);

    [Test]
    [Arguments("before-start")]
    [Arguments("during-query")]
    [Arguments("during-mutation")]
    public async Task Start_after_rename_reports_restart_without_retry_or_repair(string phase) {
        await using var h = new Harness();
        h.NeedsAppRestart = phase == "before-start";
        h.Cli.StatusBehavior = _ => {
            if (phase == "during-query") h.NeedsAppRestart = true;
            return Task.FromResult<ServiceSnapshot?>(phase == "during-query"
                ? Snap(unitPresent: false, state: "installed") : Snap());
        };
        h.Lane.Behavior = (_, _) => {
            h.NeedsAppRestart = true;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("daemon_renamed_restart_app", RecoverySurface.Attention));
        };
        await h.Controller.StartActionAsync(CancellationToken.None);
        await Assert.That(h.Surface.StatusMessages.Last()).IsEqualTo(DaemonLifecycleController.RenameRestartStatus);
        await Assert.That(h.Surface.StatusMessages.Any(x => x.Contains("Retry", StringComparison.Ordinal))).IsFalse();
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(phase == "during-mutation" ? 1 : 0);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(phase == "before-start" ? 0 : 1);
    }

    // ---- startup matrix ----

    [Test]
    public async Task Row1_job_running_is_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the matrix status query");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row2_loaded_inactive_plist_present_daemonPid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service start --verify request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // Never IKcapCli directly — the lane is the ONLY caller of these now.
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.InstallVerifiedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Row2b_loaded_inactive_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row3_orphan_label_is_replaced_without_asking() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the orphan-label repair");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row3_orphan_label_without_an_install_binary_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed", installBinaryPath: null));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-install-binary line");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    /// A live pid on an orphaned label could be another identity's daemon, which a replace would kill.
    [Test]
    public async Task Row3_orphan_label_with_a_live_pid_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed", daemonPid: 4242));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count >= 1, what: "the needs-repair attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Row4_no_label_plist_present_daemonPid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service start --verify request (bootstrap)");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row4b_no_label_plist_present_daemonPid_nonNull_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed", daemonPid: 777));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the coexistence attention");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_preconditions_pass_installs_verified_without_replace() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane's service install --verify request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install); // never Replace on the silent-install row
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // An unrecognized wire state must never fall through to the NotInstalled
    // (auto-install/start) branch — positive evidence only.
    [Test]
    public async Task Unrecognized_state_is_status_only_no_auto_install_or_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest unrecognized-state line");
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_no_profile_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.ProfileName = null;
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-profile line");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row5_nothing_path_unknown_is_status_only_silent_install_suppressed() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        var beforePhaseClosed = h.Controller.PhaseClosed.IsCompleted;
        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest PATH-unknown line");
        await h.Controller.PhaseClosed;
        await Assert.That(beforePhaseClosed).IsFalse();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Row4_starts_verified_even_when_terminal_PATH_is_unknown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request despite unknown PATH");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    [Test]
    public async Task Row5_nothing_installBinaryPath_null_is_status_only_no_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(installBinaryPath: null));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the honest no-install-binary line");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- carve-out: autoActionsPermanentlyClosed ----

    [Test]
    public async Task AutoActionsClosed_terminal_unreachable_admits_no_startup_matrix_but_still_closes_the_phase() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // would otherwise auto-install
        h.Start();

        h.PushUnreachable();

        await h.Controller.PhaseClosed; // must complete even though the matrix never runs
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(0); // RunStartupBranchAsync never admitted
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // Closed mode adds no separate arm — a second unreachable stays just as inert as the open-graph one.
    [Test]
    public async Task AutoActionsClosed_second_unreachable_stays_inert() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();
        await h.Controller.PhaseClosed;
        h.PushUnreachable();

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(0);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // StartActionAsync is a different code path from RunStartupBranchAsync — closing the latter must not touch it.
    [Test]
    public async Task AutoActionsClosed_user_clicked_StartAction_still_routes_through_the_lane() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — the install row
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install);
    }

    // ---- txn_active defers the startup matrix (wait and re-query, never mutate into a held flock) ----

    [Test]
    public async Task TxnActive_defers_the_matrix_until_the_one_requery_clears_it() {
        await using var h = new Harness();
        var call = 0;
        h.Cli.StatusBehavior = _ => {
            call++;
            return Task.FromResult<ServiceSnapshot?>(call == 1 ? Snap(txnActive: true) : Snap(txnActive: false));
        };
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the initial matrix query");
        await Assert.That(h.Lane.Requests).IsEmpty(); // deferred — not mutated into the held flock

        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the one bounded requery");
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the matrix proceeding once the flock cleared");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install);
    }

    [Test]
    public async Task TxnActive_still_active_after_the_one_requery_takes_no_action_this_run() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnActive: true));
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the initial matrix query");
        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the one bounded requery");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- a racing (meaningfully different) event forces one re-evaluation instead of silence ----

    [Test]
    public async Task Racing_connected_during_the_startup_query_forces_a_fresh_reevaluation_in_attached_mode() {
        await using var h = new Harness();
        var first = new TaskCompletionSource<ServiceSnapshot?>();
        var call = 0;
        h.Cli.StatusBehavior = _ => {
            call++;
            return call == 1 ? first.Task : Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 200));
        };
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the first (pending) status query");

        h.PushConnected(); // races the in-flight query with a MEANINGFULLY different outcome
        first.SetResult(Snap(state: "running", jobPid: 1, daemonPid: 1)); // release the now-stale first query

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 3, what: "the passive read of the racing Connected plus the forced re-evaluation against fresh state");

        // The re-evaluation must reconcile in ATTACHED mode (we're now actually Connected) — the
        // ownership-mismatch check only fires while attached, so this proves the race does not
        // strand the run's only reconciliation pass in permanently-unattached mode.
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the attached-mode ownership-mismatch attention");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("100");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("200");
    }

    // ---- startup phase closes on the FIRST terminal outcome ----

    // ---- recovery: a daemon that goes away after the startup arm gets the same ensure ----

    [Test]
    public async Task A_daemon_that_goes_away_after_connecting_is_reinstalled_through_the_lane() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the recovery install");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(2);
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task A_stopped_service_found_on_recovery_is_started_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushConnected();
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the recovery start");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    /// One recovery per interval: a trigger inside it waits for the interval's end, and a second
    /// trigger while one waits adds nothing.
    [Test]
    public async Task A_recovery_inside_the_interval_is_deferred_to_its_end_and_runs_once() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable(); // the startup arm: the first ensure, stamping the interval
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install");
        await h.Controller.PhaseClosed;
        h.PushConnected();
        var timersBefore = h.Time.TimersCreated;

        h.PushUnreachable();
        h.PushUnreachable();
        await WaitUntilAsync(() => h.Time.TimersCreated == timersBefore + 1, what: "the deferred recovery's wait");
        await Task.Delay(50); // a negative: give an undeferred recovery every chance to run
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);

        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);

        await WaitUntilAsync(() => h.Lane.Requests.Count == 2, what: "the deferred recovery");
        await Task.Delay(50);
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(2);
        await Assert.That(h.Lane.Requests[1].Verb).IsEqualTo(MutationVerb.Install);
    }

    [Test]
    public async Task A_deferred_recovery_is_skipped_when_the_daemon_came_back_meanwhile() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install");
        await h.Controller.PhaseClosed;
        var timersBefore = h.Time.TimersCreated;

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Time.TimersCreated == timersBefore + 1, what: "the deferred recovery's wait");
        var statusCalls = h.Cli.StatusCallCount;
        h.PushConnected(); // a service manager restart brought it back
        await WaitUntilAsync(() => h.Cli.StatusCallCount > statusCalls, what: "the passive read of the later Connected");
        statusCalls = h.Cli.StatusCallCount;

        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);
        await Task.Delay(50); // a negative: give the skipped recovery every chance to run

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCalls);
    }

    [Test]
    public async Task A_deferred_recovery_is_skipped_when_the_daemon_came_back_incompatible() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install");
        await h.Controller.PhaseClosed;
        var timersBefore = h.Time.TimersCreated;

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Time.TimersCreated == timersBefore + 1, what: "the deferred recovery's wait");
        h.PushUnreachable(reason: "daemon_incompatible");
        var statusCalls = h.Cli.StatusCallCount;

        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);
        await Task.Delay(50); // a negative: give the skipped recovery every chance to run

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCalls);
    }

    /// Each ensure kicks a reattach whose failure is the next unreachable, so recoveries no connect
    /// follows space out instead of repeating every interval.
    [Test]
    public async Task Recoveries_without_a_connect_between_them_back_off() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install");
        await h.Controller.PhaseClosed;

        var timersBefore = h.Time.TimersCreated;
        h.PushUnreachable();
        await WaitUntilAsync(() => h.Time.TimersCreated == timersBefore + 1, what: "the first recovery's wait");
        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 2, what: "the first recovery");

        timersBefore = h.Time.TimersCreated;
        h.PushUnreachable();
        await WaitUntilAsync(() => h.Time.TimersCreated == timersBefore + 1, what: "the second recovery's wait");
        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);
        await Task.Delay(50); // a negative: one interval is no longer enough
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(2);

        h.Clock.Advance(DaemonLifecycleController.RecoveryInterval);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 3, what: "the doubled recovery");
    }

    [Test]
    public async Task The_recovery_interval_doubles_up_to_its_cap() {
        await Assert.That(DaemonLifecycleController.CurrentRecoveryInterval(0)).IsEqualTo(DaemonLifecycleController.RecoveryInterval);
        await Assert.That(DaemonLifecycleController.CurrentRecoveryInterval(2)).IsEqualTo(DaemonLifecycleController.RecoveryInterval * 4);
        await Assert.That(DaemonLifecycleController.CurrentRecoveryInterval(100)).IsEqualTo(DaemonLifecycleController.MaxRecoveryInterval);
    }

    [Test]
    public async Task AutoActionsClosed_a_daemon_that_goes_away_after_connecting_is_not_recovered() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushConnected();
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Cli.StatusCallCount >= 1, what: "the reconciliation query");
        var statusCalls = h.Cli.StatusCallCount;

        h.PushUnreachable();
        await Task.Delay(50); // a negative: give a would-be recovery every chance to fire

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCalls);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task A_later_incompatible_daemon_is_not_recovered() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushConnected();
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");

        h.PushUnreachable(reason: "daemon_incompatible");
        await Task.Delay(50); // a negative: give a would-be recovery every chance to fire

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Incompatible_first_runs_reconciliation_and_a_later_unreachable_runs_the_ensure() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnMarker: true, txnActive: false));
        h.Start();

        h.PushUnreachable(reason: "daemon_incompatible");
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "reconciliation runs on the incompatible path too");
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention it found");

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the recovery's own status read");
    }

    // ---- the startup arm ----

    /// The second trigger lands inside the recovery interval the startup arm stamped, so it waits
    /// rather than querying again.
    [Test]
    public async Task Two_daemon_unreachable_in_a_row_consult_status_exactly_once() {
        await using var h = new Harness();
        var gate = new TaskCompletionSource<ServiceSnapshot?>();
        h.Cli.StatusBehavior = _ => gate.Task; // hangs until released below

        h.Start();
        h.PushUnreachable(); // arm claimed synchronously before this await
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the first (pending) status query");

        h.PushUnreachable(); // arm already claimed — must not issue a second query
        await Task.Delay(50);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);

        gate.SetResult(Snap(state: "running", jobPid: 1, daemonPid: 1)); // release — a no-op row either way
        await h.Controller.PhaseClosed;
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
    }

    /// A reattach after the first Connected (the daemon relaunching itself onto a new binary, a
    /// reconnect) costs one passive status read for the spawn-type indicator: no dialog, no mutation.
    [Test]
    public async Task Later_connected_transitions_only_read_status_and_neither_prompt_nor_mutate() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushConnected();
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");

        h.PushConnecting();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the passive spawn-type read of the later Connected");
        await Task.Delay(50); // a negative: give a would-be third read every chance to fire

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(2);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- reconciliation on immediate Connected ----

    [Test]
    public async Task Reconciliation_on_connected_nonOwning_loaded_label_is_attention() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 200));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the ownership-mismatch attention");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("100");
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("200");
    }

    [Test]
    public async Task Reconciliation_on_connected_stale_txn_marker_is_attention_with_a_repair_offer() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(txnMarker: true, txnActive: false) with { TxnOperation = "replace", TxnPhase = "bootstrapped" });
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
        var message = h.Surface.AttentionMessages[0];
        await Assert.That(message).Contains("'daemon-a'");
        await Assert.That(message).Contains("service replace");
        await Assert.That(message).Contains("'bootstrapped'");
        await Assert.That(message).Contains("Repair");
        await Assert.That(h.RepairOffer()).IsEqualTo(message);
    }

    [Test]
    public async Task Repair_confirmed_replaces_the_service_and_withdraws_the_banner() {
        await using var h = new Harness();
        var markerLeft = true;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: markerLeft));
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Lane.Behavior = (_, _) => {
            markerLeft = false;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
        var message = h.Surface.AttentionMessages[0];

        await h.Controller.RepairStaleMarkerAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(h.Surface.Withdrawn).Contains(message);
        await Assert.That(h.RepairOffer()).IsNull();
    }

    [Test]
    public async Task A_later_passive_read_without_the_marker_withdraws_the_offer() {
        await using var h = new Harness();
        var markerLeft = true;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: markerLeft));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
        var message = h.Surface.AttentionMessages[0];

        markerLeft = false;
        h.PushConnecting();
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Withdrawn.Contains(message), what: "the withdrawn stale-marker line");
        await Assert.That(h.RepairOffer()).IsNull();
    }

    [Test]
    public async Task An_older_read_finishing_late_never_withdraws_a_newer_offer() {
        await using var h = new Harness();
        var calls = 0;
        var slowOld = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Cli.StatusBehavior = _ => ++calls == 2
            ? slowOld.Task
            : Task.FromResult<ServiceSnapshot?>(
                Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: true));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the first stale-marker attention");

        h.PushConnecting();
        h.PushConnected();
        await WaitUntilAsync(() => calls == 2, what: "the passive read to start");

        await h.Controller.RepairStaleMarkerAsync(CancellationToken.None);
        slowOld.SetResult(Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7));
        await Task.Delay(50);

        await Assert.That(h.Surface.Withdrawn).IsEmpty();
        await Assert.That(h.RepairOffer()).IsNotNull();
    }

    [Test]
    public async Task A_marker_first_seen_on_a_passive_read_is_offered() {
        await using var h = new Harness();
        var markerLeft = false;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: markerLeft));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation read");

        markerLeft = true;
        h.PushConnecting();
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
        await Assert.That(h.RepairOffer()).IsEqualTo(h.Surface.AttentionMessages[0]);
    }

    [Test]
    public async Task Repair_settled_while_the_prompt_was_open_replaces_nothing() {
        await using var h = new Harness();
        var markerLeft = true;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: markerLeft));
        h.Surface.ConfirmBehavior = (_, _) => {
            markerLeft = false; // `kcap daemon service status` settled it meanwhile
            return Task.FromResult(true);
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");

        await h.Controller.RepairStaleMarkerAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.RepairOffer()).IsNull();
    }

    [Test]
    public async Task Repair_declined_changes_nothing_and_keeps_the_offer() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 7, daemonPid: 7, txnMarker: true));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");

        await h.Controller.RepairStaleMarkerAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.Withdrawn).IsEmpty();
        await Assert.That(h.RepairOffer()).IsNotNull();
    }

    [Test]
    public async Task Repair_warns_when_agents_are_running() {
        await Assert.That(DaemonLifecycleController.RepairInterruptsWarning(0)).IsNull();
        await Assert.That(DaemonLifecycleController.RepairInterruptsWarning(2)).Contains("2 running agents");
    }

    [Test]
    public async Task Reconciliation_on_connected_txnActive_schedules_one_requery_no_attention_yet() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnActive: true));
        h.Start();

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();

        await WaitUntilAsync(() => h.Time.TimersCreated >= 1, what: "the txn-active requery timer to be armed");
        h.Clock.Advance(TxnActiveRequeryDelay);
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "the single scheduled requery");

        // No further scheduling — a second advance must not trigger a third query.
        h.Clock.Advance(TxnActiveRequeryDelay);
        await Task.Delay(50);
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(2);
    }

    // ---- routing through the lane ----

    [Test]
    public async Task Auto_start_routes_through_the_lane_with_the_pinned_identity() {
        await using var h = new Harness();
        h.Client.DaemonName = "daemon-xyz";
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request");
        var request = h.Lane.Requests[0];
        await Assert.That(request.Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(request.Profile).IsEqualTo("default");
        await Assert.That(request.CanonicalServer).IsEqualTo(h.CanonicalServer);
        await Assert.That(request.DaemonName).IsEqualTo("daemon-xyz");
        await Assert.That(h.Cli.StartVerifiedCallCount).IsEqualTo(0); // never IKcapCli directly
    }

    [Test]
    public async Task No_canonical_server_refuses_without_ever_calling_the_lane() {
        await using var h = new Harness(canonicalServer: null);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the no-server status line");
        await Assert.That(h.Lane.Requests).IsEmpty(); // the guard runs BEFORE the lane is ever touched
        await Assert.That(h.Surface.StatusMessages[0]).Contains("no_server_configured");
    }

    // Single-presentation rule: an outcome the lane already routes to the outcome channel
    // (AttentionSkew, here) must never ALSO be raised directly by the controller.
    [Test]
    public async Task AttentionSkew_outcome_does_not_raise_the_controllers_direct_attention_surface() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.AttentionSkew("ownership_mismatch"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Attention/Status every chance to appear
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        // The reattach kick is unconditional after any lane mutation call: a mutation
        // attempt may have restarted the daemon even though this outcome isn't itself a success.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    // An UnconfirmedNoAttach outcome (the lane's TimedOut classification) makes no local surface
    // call at all: channel-only, same as every other non-success outcome.
    [Test]
    public async Task UnconfirmedNoAttach_outcome_produces_no_controller_surface_call_but_still_kicks_reattach() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.UnconfirmedNoAttach());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // Any lane mutation attempt may have restarted the daemon, so the kick is
        // unconditional — not gated on this outcome being a success.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    // A Refused outcome that reached the LANE (as opposed to the guard
    // refusing before ever touching it — see No_canonical_server_refuses_without_ever_calling_the_lane
    // above) was already enqueued onto the outcome channel by the lane's own Deliver — the
    // controller must make NO surface call of its own, or the composition-root consumer's
    // presentation doubles up.
    [Test]
    public async Task Refused_outcome_from_the_lane_produces_no_controller_surface_call() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Status/Attention every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    // ---- UX confirmation ----

    [Test]
    public async Task Successful_mutation_kicks_restart_no_status_message() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request to begin");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);

        release.SetResult(new MutationOutcome.Succeeded());

        await WaitUntilAsync(() => h.Client.RestartCount >= 1, what: "the post-mutation reattach kick");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing message every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task SucceededAfterTimeout_also_kicks_restart_no_status_message() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.SucceededAfterTimeout());
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Client.RestartCount >= 1, what: "the post-mutation reattach kick");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
    }

    // ---- coded failure ----

    // A Failed outcome from the lane is presented ONLY by the composition-root consumer (see AppMutationLaneWiringTests.PresentOutcomeAsync's Attention/Reinstall/
    // Takeover coverage for the actual message content) — the controller itself makes no surface
    // call, and a second daemon_unreachable inside the recovery interval does not retry yet.
    [Test]
    public async Task Failed_outcome_from_the_lane_produces_no_controller_surface_call_and_no_immediate_retry() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(24, "gave_up_waiting", RecoverySurface.Attention));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane request");
        await h.Controller.PhaseClosed;
        await Task.Delay(50); // give a wrongly-firing Status/Attention every chance to appear
        await Assert.That(h.Surface.StatusMessages).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
        // A Failed outcome may still mean the daemon got restarted mid-mutation — the
        // kick is unconditional, not gated on Succeeded/SucceededAfterTimeout.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);

        h.PushUnreachable(); // deferred to the interval's end, which the fake clock never reaches
        await Task.Delay(50);
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Client.RestartCount).IsEqualTo(1); // no second lane call — no second kick
    }

    // ---- version caching ----

    [Test]
    public async Task Start_caches_the_cli_version_once() {
        await using var h = new Harness();
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>("9.9.9");
        h.Start();

        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the one-shot version probe");
        await Assert.That(h.Controller.CliVersion).IsEqualTo("9.9.9");
    }

    // ---- quiescence ----

    [Test]
    public async Task QuiescedAsync_waits_for_an_in_flight_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        h.PushUnreachable();
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the in-flight mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        release.SetResult(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        await quiesced;
    }

    // ---- disposal ----

    [Test]
    public async Task DisposeAsync_is_idempotent() {
        await using var h = new Harness();
        h.Start();

        await h.Controller.DisposeAsync();
        await h.Controller.DisposeAsync(); // must not throw ObjectDisposedException
    }

    // ---- Start action ----

    [Test]
    public async Task StartAction_job_running_kicks_reattach_without_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 1, daemonPid: 1));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages[0]).Contains("Reconnecting");
    }

    [Test]
    public async Task StartAction_nothing_at_all_installs_the_service() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages[0]).Contains("Waiting for it to connect");
    }

    /// A click is the user asking, so an unknown terminal PATH does not stop the install.
    [Test]
    public async Task StartAction_nothing_at_all_installs_even_when_terminal_PATH_is_unknown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Probe.TerminalPathBehavior = _ => Task.FromResult<string?>(null);
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Install);
    }

    [Test]
    [Arguments("installed")]     // orphan label
    [Arguments("not_installed")] // nothing at all
    public async Task StartAction_unit_rewrite_without_an_install_binary_leaves_the_service_alone(string state) {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: state, installBinaryPath: null));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Single()).Contains("the daemon service was left as it is");
    }

    [Test]
    public async Task StartAction_without_a_profile_does_not_install() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.ProfileName = null;
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Single()).Contains("the daemon service was left as it is");
    }

    [Test]
    public async Task StartAction_loaded_plist_present_pid_null_starts_verified() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_loaded_pid_nonNull_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_orphan_label_is_replaced_without_asking() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
    }

    [Test]
    public async Task StartAction_no_label_plist_present_pid_null_starts_verified_bootstrap() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the lane StartVerified request (bootstrap)");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.StartVerified);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_no_label_plist_present_pid_nonNull_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "not_installed", daemonPid: 777));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_repair_accept_calls_replace() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the repair request");
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.Replace);

        release.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;
    }

    [Test]
    public async Task StartAction_repair_decline_does_not_mutate() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed", daemonPid: 555));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task StartAction_status_unknown_takes_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    // Counterpart for the Start action's own state-classification switch.
    [Test]
    public async Task StartAction_unrecognized_state_is_status_only_no_action() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "some_future_state"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAction_generic_exception_is_status_surfaced_not_thrown() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => throw new InvalidOperationException("boom");
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None); // must not throw

        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
    }

    // Holds the gate open with a pending startup-triggered install (TCS-scripted), then invokes
    // StartActionAsync concurrently: it must block on the gate rather than act on stale evidence,
    // and once the gate clears it re-queries (a SECOND ServiceStatusAsync call) before acting —
    // never reusing whatever it might have read before the wait.
    [Test]
    public async Task StartAction_racing_auto_install_awaits_the_gate_then_reQueries_fresh_evidence() {
        await using var h = new Harness();
        var install = new TaskCompletionSource<MutationOutcome>();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — the install row
        h.Lane.Behavior = (_, _) => install.Task;
        h.Start();

        h.PushUnreachable(); // claims the arm, starts the startup matrix, blocks on the install call
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the startup install to begin (holding the gate)");
        var statusCallsBeforeStart = h.Cli.StatusCallCount;

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await Task.Delay(50); // give a wrongly-unblocked Start every chance to act early
        await Assert.That(startTask.IsCompleted).IsFalse();
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(statusCallsBeforeStart); // no re-query yet — still blocked

        install.SetResult(new MutationOutcome.Failed(21, "verify_viability", RecoverySurface.Attention));
        await startTask;

        await Assert.That(h.Cli.StatusCallCount).IsGreaterThan(statusCallsBeforeStart); // a fresh query after the gate cleared
    }

    [Test]
    public async Task QuiescedAsync_waits_for_a_startAction_mutation() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        var release = new TaskCompletionSource<MutationOutcome>();
        h.Lane.Behavior = (_, _) => release.Task;
        h.Start();

        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the Start-triggered mutation");

        var quiesced = h.Controller.QuiescedAsync();
        await Task.Delay(50);
        await Assert.That(quiesced.IsCompleted).IsFalse();

        release.SetResult(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        await quiesced;
        await startTask;
    }

    // ---- background-priority indicator ----

    [Test]
    [Arguments("adaptive", true)]
    [Arguments("background", true)]
    [Arguments("daemon", false)]
    [Arguments("interactive", false)]
    public async Task Indicator_follows_the_classified_spawn_type(string word, bool expected) {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = word });
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the connected read");
        await Assert.That(h.Background()).IsEqualTo(expected);
    }

    [Test]
    public async Task Indicator_is_left_alone_by_an_unknown_word_an_empty_word_or_a_failed_read() {
        // Off Windows, so a failed read is not followed by the Windows service offer's own read
        await using var h = new Harness { OffersSupervision = false };
        var words = new Queue<string?>(["adaptive", "app", "", null]);
        h.Cli.StatusBehavior = _ => {
            var word = words.Dequeue();
            return Task.FromResult<ServiceSnapshot?>(word is null ? null : Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = word });
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "first read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 2, what: "unknown-word read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 3, what: "empty-word read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 4, what: "failed read");
        await Assert.That(h.Background()).IsTrue();
    }

    [Test]
    public async Task Every_transition_into_connected_reads_status_without_arming_a_mutation() {
        await using var h = new Harness();
        var words = new Queue<string>(["adaptive", "adaptive", "daemon"]);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = words.Dequeue() });
        h.Start();
        h.PushUnreachable();
        await WaitUntilAsync(() => h.Cli.StatusCallCount >= 1, what: "the startup read");
        var afterStartup = h.Cli.StatusCallCount;

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == afterStartup + 1, what: "the connected read");
        await Assert.That(h.Background()).IsTrue();

        h.PushConnecting(); h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == afterStartup + 2, what: "the reconnect read");
        await Assert.That(h.Background()).IsFalse();
        await Assert.That(h.Lane.Requests.Count(r => r.Verb != MutationVerb.StartVerified && r.Verb != MutationVerb.Install)).IsEqualTo(0);
    }

    // ---- the reload action ----

    [Test]
    public async Task Reload_prompts_on_every_click_and_a_decline_runs_nothing() {
        await using var h = new Harness();
        h.Start();
        h.PushSnapshot(activeAgents: 3);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(2);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindReloadService);
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.ReloadDisclosure(3, resumes: false));
        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_runs_the_verb_records_success_and_rereads_status() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Select(r => r.Verb)).IsEquivalentTo([MutationVerb.Reload]);
        await Assert.That(h.Surface.StatusMessages).Contains(DaemonLifecycleController.StandardPriorityStatus);
        await Assert.That(h.Reload()).IsNull();
        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Background()).IsFalse();
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
    }

    [Test]
    public async Task Reload_cancelled_by_the_lane_completes_quietly_and_releases_the_claim() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Lane.Behavior = (_, _) => Task.FromException<MutationOutcome>(new TaskCanceledException());

        await h.Controller.ReloadServiceAsync(CancellationToken.None); // must not throw

        await Assert.That(h.Reloading()).IsFalse();

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(2); // the claim was released: the second click prompted again
        await Assert.That(h.Lane.Requests.Count).IsEqualTo(2);
        await Assert.That(h.Reload()).IsNull();
    }

    [Test]
    public async Task Reload_records_a_failure_and_a_later_success_clears_it() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var outcomes = new Queue<MutationOutcome>([
            new MutationOutcome.Failed(1, "unit_missing", RecoverySurface.Attention),
            new MutationOutcome.Succeeded()]);
        h.Lane.Behavior = (_, _) => Task.FromResult(outcomes.Dequeue());

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("unit_missing");
        await Assert.That(h.Reload()!.ExitCode).IsEqualTo(1);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()).IsNull();
    }

    [Test]
    public async Task A_success_followed_by_a_failure_leaves_the_failure_standing() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var outcomes = new Queue<MutationOutcome>([new MutationOutcome.Succeeded(), new MutationOutcome.AttentionSkew("ownership_mismatch")]);
        h.Lane.Behavior = (_, _) => Task.FromResult(outcomes.Dequeue());

        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Reload()!.Token).IsEqualTo("ownership_mismatch");
    }

    [Test]
    public async Task Second_call_while_the_first_prompt_is_open_is_ignored_and_a_decline_releases_the_claim() {
        await using var h = new Harness();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Surface.ConfirmBehavior = (_, _) => answer.Task;
        var gate = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.Behavior = (_, _) => gate.Task;

        var first  = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the first prompt");
        var second = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await second; // returns at once: no prompt, no request
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Reloading()).IsTrue();

        answer.SetResult(true);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the single lane request");
        var third = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await third;
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);

        gate.SetResult(new MutationOutcome.Succeeded());
        await first;
        await Assert.That(h.Reloading()).IsFalse();

        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(2);
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_cancels_when_the_attach_changed_while_the_prompt_was_open() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the connected read");
        h.Surface.ConfirmBehavior = (_, _) => { h.PushConnecting(); return Task.FromResult(true); };

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Last()).IsEqualTo(DaemonLifecycleController.PromptStaleStatus);
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_cancels_when_the_attach_changed_while_waiting_for_the_gate() {
        await using var h = new Harness();
        var slowArm = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Cli.StatusBehavior = _ => ++reads == 1 ? slowArm.Task : Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => reads == 1, what: "the arm's read holding the gate");
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

        var reload = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the consent, after which the reload waits on the gate");
        h.PushConnecting(); h.PushConnected(); // the attachment changes while the gate is held
        slowArm.SetResult(Snap(state: "running", jobPid: 100, daemonPid: 100));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Surface.StatusMessages.Last()).IsEqualTo(DaemonLifecycleController.PromptStaleStatus);
        await Assert.That(h.Reloading()).IsFalse();
    }

    [Test]
    public async Task Reload_without_a_canonical_server_records_the_refusal_and_releases_the_claim() {
        await using var h = new Harness(canonicalServer: null);
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests).IsEmpty();
        await Assert.That(h.Reload()!.Token).IsEqualTo("no_server_configured");
        await Assert.That(h.Reload()!.Kind).IsEqualTo(ReloadOutcomeKind.Refused);
        await Assert.That(h.Reloading()).IsFalse();
    }

    // ---- resolution by passive reads ----

    [Test]
    public async Task A_positive_read_after_a_priority_class_failure_clears_it_but_leaves_other_failures() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None); // the action's own re-read runs after the record
        await Assert.That(h.Reload()).IsNull();

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "unit_missing", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("unit_missing");

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.AttentionRepair("running_without_daemon_pid"));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("running_without_daemon_pid");
    }

    [Test]
    public async Task A_read_that_started_before_the_failure_was_recorded_does_not_clear_it() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        var slowRead = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Cli.StatusBehavior = _ => ++reads switch {
            1 => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "adaptive" }),
            2 => slowRead.Task,
            3 => Task.FromResult<ServiceSnapshot?>(null),
            _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" }),
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(h.Background, what: "the arm read marking the band");
        h.PushConnecting(); h.PushConnected(); // read 2: a passive read that stays in flight across the record
        await WaitUntilAsync(() => reads == 2, what: "the slow read starting");

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None); // read 3 is its follow-up, and reads nothing
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        slowRead.SetResult(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        await WaitUntilAsync(() => !h.Background(), what: "the slow read lowering the indicator");
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        h.PushConnecting(); h.PushConnected(); // a read that starts after the record
        await WaitUntilAsync(() => h.Reload() is null, what: "the later positive read clearing the failure");
    }

    [Test]
    public async Task Disclosure_names_the_count_and_says_the_agents_resume() {
        var text = DaemonLifecycleController.ReloadDisclosure(2, resumes: true);
        await Assert.That(text).IsEqualTo("Reloading restarts the daemon now. Its 2 running agents are stopped, even mid-turn. Eligible agents resume if auto-resume is on. A review or evaluation still running when it exits may end.");
        await Assert.That(DaemonLifecycleController.ReloadDisclosure(1, resumes: true)).Contains("Its 1 running agent is stopped");
        await Assert.That(DaemonLifecycleController.ReloadDisclosure(0, resumes: true)).IsEqualTo("Reloading restarts the daemon now. A review or evaluation still running when it exits may end.");
    }

    /// A daemon without the shutdown frame is restarted the old way and keeps no resume ledger.
    [Test]
    public async Task Disclosure_for_a_daemon_that_cannot_keep_its_agents_warns_they_end() {
        await Assert.That(DaemonLifecycleController.ReloadDisclosure(2, resumes: false)).IsEqualTo("Reloading restarts the daemon and stops its 2 agents. Any other agents, launches or evaluations running when it exits also end. Uncommitted work in their worktrees is lost.");
    }

    [Test]
    public async Task Reload_prompt_promises_a_resume_only_when_the_daemon_advertises_it() {
        await using var h = new Harness();
        h.Start();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [DaemonLifecycleController.ResumeCapability]));
        h.PushSnapshot(activeAgents: 2);
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        h.PushConnected();
        await h.Controller.ReloadServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.ReloadDisclosure(2, resumes: true));
        await Assert.That(h.Surface.Prompts[1].Disclosure).IsEqualTo(DaemonLifecycleController.ReloadDisclosure(2, resumes: false));
    }

    [Test]
    public async Task Disposal_during_a_reload_cancels_its_mutation_wait_and_quiesces() {
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Lane.Behavior = (_, ct) => {
            var tcs = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };

        var reload = h.Controller.ReloadServiceAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the reload reaching the lane");

        await h.Controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(h.Reloading()).IsFalse();
        await Assert.That(h.Client.RestartCount).IsEqualTo(0);
        await Assert.That(h.Reload()).IsNull();
    }

    [Test]
    public async Task An_older_read_finishing_last_does_not_overwrite_newer_priority_evidence() {
        await using var h = new Harness();
        var slow = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        h.Cli.StatusBehavior = _ => ++reads switch {
            1 => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "adaptive" }),
            2 => slow.Task,
            _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" }),
        };
        var seen = new List<bool>();
        using var watching = h.Controller.BackgroundPriority.Subscribe(seen.Add);
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(h.Background, what: "the arm read marking the band");
        h.PushConnecting(); h.PushConnected(); // read 2 stays in flight
        await WaitUntilAsync(() => reads == 2, what: "the older read starting");
        h.PushConnecting(); h.PushConnected(); // read 3 starts later and finishes first
        await WaitUntilAsync(() => !h.Background(), what: "the newer positive read lowering the indicator");

        slow.SetResult(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "adaptive" });
        await Task.Delay(100);

        await Assert.That(h.Background()).IsFalse();
        await Assert.That(seen.SkipWhile(x => !x).SkipWhile(x => x).All(x => !x)).IsTrue(); // never raised again once lowered
    }

    [Test]
    public async Task A_snapshot_with_nothing_loaded_lowers_the_indicator() {
        await using var h = new Harness();
        var reads = 0;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(++reads == 1
            ? Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "adaptive" }
            : Snap(unitPresent: true, state: "not_installed"));
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(h.Background, what: "the arm read marking the band");

        h.PushConnecting(); h.PushConnected();

        await WaitUntilAsync(() => !h.Background(), what: "the not-loaded read lowering the indicator");
    }

    // ---- harness ----

    /// Records every MutationRequest the controller hands to `_runMutation` and lets a test
    /// script an outcome per request (blanket Behavior, TCS-friendly) — the fake lane seam Task
    /// 10's controller tests drive instead of a raw IKcapCli mutation call. Defaults to an
    /// immediate Succeeded so tests that don't care about the mutation's own outcome (most of the
    /// "no mutation happens" tests never even call this) aren't forced to script one.
    static DaemonStatusDto ForeignDaemon(string server = "https://norton.kcap.ai:443", string? profile = "norton", int active = 0) =>
        FakeDaemonClientService.Snap(daemon: "daemon-a", serverUrl: server, profile: profile, active: active);

    /// A daemon attached by name that serves another server or profile is reported, and an idle one is offered
    /// the ordinary Replace, which the lane runs for the app's own profile.
    [Test]
    [Arguments("https://norton.kcap.ai:443", "norton", "Daemon daemon-a doesn't match this app. It's running for norton.kcap.ai, profile norton, not kcap.example.com, profile default.")]
    [Arguments("https://kcap.example.com:443", "norton", "Daemon daemon-a doesn't match this app. It's running for profile norton, not profile default.")]
    public async Task A_daemon_serving_another_identity_is_reported_and_offered_a_replace(string server, string profile, string message) {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(server, profile));
        h.PushConnected();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the replace");
        // The report carries the command too, so a declined or failed replace still leaves a fix.
        await Assert.That(h.Surface.AttentionMessages.Any(m => m.StartsWith(message, StringComparison.Ordinal) && m.Contains("kcap daemon service", StringComparison.Ordinal))).IsTrue();
        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(h.Lane.Requests.Single().Profile).IsEqualTo("default");
    }

    /// A reconnect while the offer waits on the gate is checked afresh rather than suppressed as already reported.
    [Test]
    public async Task A_reconnect_while_the_offer_waits_for_the_gate_still_gets_one() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        var running = false;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: running ? "running" : "installed"));
        var release = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        h.Lane.Behavior = (_, _) => Interlocked.Increment(ref calls) == 1
            ? release.Task
            : Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Start();
        var startTask = h.Controller.StartActionAsync(CancellationToken.None);
        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "a Start holding the gate");

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon());
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        h.PushConnecting();
        h.PushConnected();
        await Task.Delay(50);
        running = true;
        release.SetResult(new MutationOutcome.Failed(24, "verify_readiness_timeout", RecoverySurface.Attention));
        await startTask;

        await WaitUntilAsync(() => h.Surface.Prompts.Any(p => p.Kind == LifecyclePrompt.KindRepair), what: "the replace offer");
        await Assert.That(h.Surface.AttentionMessages.Count(m => m.StartsWith("Daemon daemon-a doesn't match", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// The replace installs the app's own kcap, so an offer to a daemon newer than it says the replace is a downgrade.
    [Test]
    public async Task A_replace_offer_to_a_newer_daemon_says_it_downgrades() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>("1.4.0+abc");
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Start();
        await WaitUntilAsync(() => h.Controller.CliVersion is not null, what: "the version probe");

        h.Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
            daemon: "daemon-a", version: "1.4.1-beta.1", serverUrl: "https://norton.kcap.ai:443", profile: "norton"));
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the replace offer");
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("The daemon runs 1.4.1-beta.1, newer than this kcap's 1.4.0: replacing it now downgrades it to 1.4.0.");
    }

    /// The replace runs whichever kcap the lane resolves when it runs, so the offer names the version probed for
    /// it then, not the one cached at startup.
    [Test]
    public async Task A_replace_offer_names_the_kcap_probed_for_it_not_the_cached_one() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>("1.4.0+abc");
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Start();
        await WaitUntilAsync(() => h.Controller.CliVersion is not null, what: "the version probe");
        h.Cli.VersionBehavior = _ => Task.FromResult<string?>("1.5.0");

        h.Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
            daemon: "daemon-a", version: "1.4.1", serverUrl: "https://norton.kcap.ai:443", profile: "norton"));
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the replace offer");
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("The daemon runs 1.4.1; replacing it installs 1.5.0.");
    }

    /// A snapshot outlives the attach that delivered it, so once an attach fails the version is that attach's own.
    [Test]
    public async Task A_failed_attach_reports_its_own_daemon_version_not_the_last_snapshot() {
        await using var h = new Harness();
        h.Start();

        h.Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(daemon: "daemon-a", version: "1.4.1", serverUrl: h.CanonicalServer!));
        h.PushConnected();
        await Assert.That(h.Controller.AttachedDaemonVersion).IsEqualTo("1.4.1");

        h.PushUnreachable("daemon_incompatible");
        await Assert.That(h.Controller.AttachedDaemonVersion).IsNull();

        h.PushUnreachable("daemon_incompatible", daemonVersion: "1.3.0");
        await Assert.That(h.Controller.AttachedDaemonVersion).IsEqualTo("1.3.0");

        h.PushConnecting();
        await Assert.That(h.Controller.AttachedDaemonVersion).IsNull();
    }

    [Test]
    public async Task A_prompt_is_left_alone_when_the_versions_are_unknown_or_the_same() {
        var prompt = new LifecyclePrompt(LifecyclePrompt.KindRepair, null, "1.4.0", false, "Replace it.");

        await Assert.That(DaemonLifecycleController.WithVersionChange(prompt, null, "1.4.0")).IsSameReferenceAs(prompt);
        await Assert.That(DaemonLifecycleController.WithVersionChange(prompt, "1.4.0+abc", "1.4.0+def")).IsSameReferenceAs(prompt);
        await Assert.That(DaemonLifecycleController.WithVersionChange(prompt, "1.3.9", "1.4.0").Disclosure)
            .IsEqualTo("Replace it.\n\nThe daemon runs 1.3.9; replacing it installs 1.4.0.");
    }

    /// Where the platform has no verified replace, a mismatch is reported with the command to run by hand.
    [Test]
    public async Task Without_a_verified_replace_the_mismatch_is_only_reported() {
        if (AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon());
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        await Assert.That(h.Surface.AttentionMessages.Single()).Contains("kcap daemon service uninstall --name daemon-a");
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    /// A busy daemon is reported but not offered a replace, which would end its agents; once they finish,
    /// without a reconnect, the offer follows.
    [Test]
    public async Task A_busy_foreign_daemon_is_offered_a_replace_once_its_agents_finish() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(active: 2));
        h.PushConnected();

        const string held = "The app offers to replace it once its 2 running agents finish.";
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Any(m => m.EndsWith(held, StringComparison.Ordinal)), what: "the report");
        await Assert.That(h.Surface.Prompts).IsEmpty();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(active: 0));

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the replace once idle");
        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindRepair);
    }

    [Test]
    public async Task Activity_is_cleared_when_the_attach_drops() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100));
        h.Start();
        h.PushConnected();
        h.PushSnapshot(2);

        await Assert.That(h.Controller.LatestActivity).IsEqualTo(new DaemonActivity(2, true));

        h.PushUnreachable();

        await Assert.That(h.Controller.LatestActivity).IsNull();
    }

    /// A mismatch the daemon no longer has is taken back from wherever the report still shows, the tray included.
    [Test]
    public async Task A_resolved_mismatch_withdraws_its_report() {
        await using var h = new Harness(autoActionsPermanentlyClosed: true);
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon());
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        var report = h.Surface.AttentionMessages.Single();

        h.PushConnecting();
        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(server: "https://kcap.example.com:443", profile: "default"));
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Withdrawn.Count > 0, what: "the withdrawal");
        await Assert.That(h.Surface.Withdrawn.Single()).IsEqualTo(report);
    }

    /// A launch the daemon has admitted but not yet listed is work in progress: no replace is offered over it.
    [Test]
    public async Task A_daemon_still_starting_a_launch_is_not_offered_a_replace() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(active: 0) with { Busy = true });
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        await Task.Delay(50);
        await Assert.That(h.Surface.AttentionMessages.Any(m => m.Contains("The app offers to replace it once", StringComparison.Ordinal))).IsTrue();
        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    /// Agents that start while the prompt is open cancel the replace it was answered for.
    [Test]
    public async Task Agents_starting_while_the_prompt_is_open_cancel_the_replace() {
        if (!AttachedIdentity.SupportsVerifiedReplace) return;
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "running"));
        h.Surface.ConfirmBehavior = (_, _) => {
            h.Client.SnapshotsSubject.OnNext(ForeignDaemon(active: 1));
            return Task.FromResult(true);
        };
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon());
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Any(m => m.StartsWith("Agents started", StringComparison.Ordinal)), what: "the cancel");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    /// Under a server override the resolution names no profile, so no request can be built: the report says
    /// how to fix it by hand. Auto actions closed after an abandoned wizard report the same way.
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Without_a_profile_or_with_auto_actions_closed_the_mismatch_is_only_reported(bool urlOverride, bool closed) {
        await using var h = new Harness(autoActionsPermanentlyClosed: closed);
        if (urlOverride) h.ProfileName = null;
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon());
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        await Assert.That(h.Surface.AttentionMessages.Single())
            .Contains($"--name daemon-a --profile {(urlOverride ? "<profile>" : "default")}");
        await Assert.That(h.Surface.AttentionMessages.Single().Contains("Replace <profile>", StringComparison.Ordinal)).IsEqualTo(urlOverride);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    /// An older daemon reports no profile, and one that matches on name and server is then accepted;
    /// a reattach to the same mismatch does not report it again.
    [Test]
    public async Task A_daemon_matching_what_it_reports_is_accepted_and_a_mismatch_is_reported_once() {
        await using var h = new Harness();
        h.Start();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(server: "https://kcap.example.com:443", profile: null));
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount >= 1, what: "the reconciliation");
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();

        h.Client.SnapshotsSubject.OnNext(ForeignDaemon(active: 1));
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count > 0, what: "the report");
        h.PushConnecting();
        h.PushConnected();
        await Task.Delay(50);
        await Assert.That(h.Surface.AttentionMessages.Count).IsEqualTo(1);
    }

    /// The command offered by hand names the daemon by its service id and the profile as the named shell
    /// reads it, and asks for the verified replace only where the platform supports it. An app under a server
    /// override has no profile, so the daemon must be given one that resolves the override's server.
    [Test]
    [Arguments("daemon-a", "default", false, true,
        "Run this in a shell without KCAP_URL set, then restart kcap: `kcap daemon service install --replace --verify --name daemon-a --profile default`")]
    [Arguments("-My Laptop", "it's mine", false, false,
        "Run these in a shell without KCAP_URL set, then restart kcap: `kcap daemon service uninstall --name my-laptop` `kcap daemon service install --name my-laptop --profile 'it'\\''s mine'`")]
    [Arguments("my laptop", "it's mine", true, true,
        "Run this in PowerShell without KCAP_URL set, then restart kcap: `kcap daemon service install --replace --verify --name 'my laptop' --profile 'it''s mine'`")]
    [Arguments("laptop", "it\u2019s", true, false,
        "Run these in PowerShell without KCAP_URL set, then restart kcap: `kcap daemon service uninstall --name laptop` `kcap daemon service install --name laptop --profile 'it\u2019\u2019s'`")]
    [Arguments("laptop", null, false, true,
        "Replace <profile> with a profile for this app's server. Run this in a shell without KCAP_URL set, then restart kcap: `kcap daemon service install --replace --verify --name laptop --profile <profile>`")]
    public async Task The_manual_repair_command_is_one_the_named_shell_runs(string name, string? profile, bool windows, bool verified, string expected) =>
        await Assert.That(AttachedIdentity.ManualRepair(name, profile, windows, verified)).IsEqualTo(expected);

    /// A configured name the socket reduces to the daemon's own is the same daemon, and two servers on one host
    /// are told apart in full.
    [Test]
    public async Task Names_compare_as_the_socket_does_and_servers_on_one_host_read_in_full() {
        var sameName = FakeDaemonClientService.Snap(daemon: "my-laptop", serverUrl: "https://kcap.example.com:443").Daemon;
        await Assert.That(AttachedIdentity.Mismatch(sameName, "My Laptop", "https://kcap.example.com:443", null)).IsNull();

        var otherPort = FakeDaemonClientService.Snap(daemon: "daemon-a", serverUrl: "https://kcap.example.com:8443").Daemon;
        await Assert.That(AttachedIdentity.Mismatch(otherPort, "daemon-a", "https://kcap.example.com:443", null))
            .IsEqualTo("Daemon daemon-a doesn't match this app. It's running for https://kcap.example.com:8443, not https://kcap.example.com:443.");
    }

    sealed class FakeMutationLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    // ---- a daemon nothing supervises ----

    [Test]
    public async Task An_unsupervised_daemon_is_reported_and_offered_to_the_service() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(daemonPid: 4242));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the supervision offer");
        await Assert.That(h.Surface.AttentionMessages).IsEquivalentTo([DaemonLifecycleController.UnsupervisedAttention]);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.UnsupervisedDisclosure);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Accepting_the_offer_replaces_the_daemon_through_the_lane_and_withdraws_the_line() {
        await using var h = new Harness();
        var replaced = false;
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            replaced ? Snap(unitPresent: true, state: "running", jobPid: 77, daemonPid: 77) : Snap(daemonPid: 4242));
        h.Lane.Behavior = (_, _) => {
            replaced = true;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Withdrawn.Count == 1, what: "the withdrawn attention line");
        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(h.Surface.Withdrawn).IsEquivalentTo([DaemonLifecycleController.UnsupervisedAttention]);
    }

    /// The takeover it leads to ends a standalone owner only on the Windows CLI, so elsewhere nothing changes
    [Test]
    public async Task Off_windows_an_unsupervised_daemon_is_neither_reported_nor_offered() {
        await using var h = new Harness { OffersSupervision = false };
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(daemonPid: 4242));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).IsEmpty();
    }

    [Test]
    public async Task A_busy_unsupervised_daemon_is_reported_but_not_offered() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(daemonPid: 4242));
        h.Start();
        h.PushSnapshot(activeAgents: 1);

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the unsupervised attention");
        await Task.Delay(50); // a negative: give a prompt every chance to appear
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    [Arguments(true, "not_installed", 4242)] // a unit on disk: the existing coexistence line, no offer
    [Arguments(true, "running", 4242)]
    [Arguments(false, "not_installed", null)]
    public async Task Supervised_or_absent_daemons_get_no_offer(bool unitPresent, string state, int? daemonPid) {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: unitPresent, state: state, jobPid: state == "running" ? daemonPid : null, daemonPid: daemonPid));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.Surface.AttentionMessages).DoesNotContain(DaemonLifecycleController.UnsupervisedAttention);
    }

    // ---- the Windows service ----

    static ServiceSnapshot TaskSupervised(bool installable = true) =>
        Snap(unitPresent: true, state: "running", jobPid: 77, daemonPid: 77) with {
            Supervisor = "windows_scheduled_task", WindowsServiceInstallable = installable,
        };

    [Test]
    public async Task A_task_supervised_daemon_is_offered_the_windows_service_and_accepting_installs_it() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the Windows service install");
        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindWindowsService);
        await Assert.That(h.Surface.Prompts.Single().Disclosure).IsEqualTo(DaemonLifecycleController.WindowsServiceDisclosure(0, resumes: false));
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.InstallWindowsService);
    }

    /// The update path: the installer stopped the daemon, so the app's first outcome is unreachable and
    /// the daemon it starts connects later. That Connected is the run's only chance to make the offer.
    [Test]
    public async Task A_daemon_that_connects_after_the_startup_branch_is_offered_the_windows_service() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Start();

        h.PushUnreachable();
        await h.Controller.PhaseClosed;
        h.PushConnected();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the Windows service install");
        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindWindowsService);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.InstallWindowsService);
    }

    /// That daemon connects mid-transaction, where the read its Connected makes can fail: the offer is settled
    /// by a read under the gate, not dropped for the run
    [Test]
    public async Task A_failed_read_when_the_started_daemon_connects_still_leads_to_the_offer() {
        await using var h = new Harness();
        var reads = 0;
        h.Cli.StatusBehavior = _ => Task.FromResult(++reads == 2 ? null : (ServiceSnapshot?)TaskSupervised());
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Start();

        h.PushUnreachable();
        await h.Controller.PhaseClosed;
        h.PushConnected();

        await WaitUntilAsync(() => h.Lane.Requests.Count == 1, what: "the Windows service install");
        await Assert.That(h.Surface.Prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindWindowsService);
    }

    [Test]
    public async Task Declining_the_windows_service_is_remembered_and_not_asked_again() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.DeclinesRemembered == 1, what: "the remembered decline");
        await Assert.That(h.Lane.Requests).IsEmpty();

        await using var later = new Harness { WindowsServiceDeclined = true };
        later.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        later.Start();
        later.PushConnected();
        await WaitUntilAsync(() => later.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await later.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(later.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task A_prompt_closed_by_the_app_shutting_down_is_not_remembered_as_a_decline() {
        var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Surface.ConfirmBehavior = async (_, ct) => {
            try {
                await Task.Delay(Timeout.Infinite, ct);
            } catch (OperationCanceledException) { }
            return false;
        };
        h.Start();
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the Windows service offer");

        await h.DisposeAsync();

        await Assert.That(h.DeclinesRemembered).IsEqualTo(0);
    }

    /// The prompt agreed to end no agents; one started while it was open would be ended by the move
    [Test]
    public async Task A_daemon_that_got_busy_while_the_prompt_was_open_is_not_moved() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Surface.ConfirmBehavior = (_, _) => {
            h.PushSnapshot(activeAgents: 1);
            return Task.FromResult(true);
        };
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Contains(DaemonLifecycleController.PromptBusyStatus), what: "the cancel");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    /// The consent was for the daemon the prompt described
    [Test]
    public async Task A_daemon_that_changed_while_the_prompt_was_open_is_not_moved() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Surface.ConfirmBehavior = (_, _) => {
            h.PushConnecting();
            return Task.FromResult(true);
        };
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.StatusMessages.Contains(DaemonLifecycleController.PromptStaleStatus), what: "the cancel");
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task An_older_installed_service_is_offered_an_update_and_a_decline_is_not_remembered() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
            Snap(unitPresent: true, state: "running", jobPid: 77, daemonPid: 77) with { Supervisor = "windows_service", ServiceUpgradeAvailable = true });
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the update offer");
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindWindowsServiceUpdate);
        await Task.Delay(50);
        await Assert.That(h.DeclinesRemembered).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)] // nothing to offer: the bundle carries no service
    [Arguments(true, true)]   // a busy daemon: the install would end its agents
    public async Task No_windows_service_offer(bool installable, bool busy) {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised(installable));
        h.Start();
        if (busy) h.PushSnapshot(activeAgents: 1);

        h.PushConnected();

        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();
        await Assert.That(h.DeclinesRemembered).IsEqualTo(0);
    }

    /// The move stops the daemon keeping its agents, so a daemon that resumes them is asked while they run
    [Test]
    public async Task A_busy_daemon_that_resumes_its_agents_is_offered_the_windows_service() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Start();
        h.PushSnapshot(activeAgents: 2);

        h.Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [DaemonLifecycleController.ResumeCapability]));

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the Windows service offer");
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.WindowsServiceDisclosure(2, resumes: true));
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("2 running agents resume if auto-resume is on");
    }

    /// One that cannot resume them waits for idle, without spending the run's offer
    [Test]
    public async Task A_busy_daemon_that_cannot_resume_is_offered_once_it_reconnects_idle() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(TaskSupervised());
        h.Start();
        h.PushSnapshot(activeAgents: 1);
        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Surface.Prompts).IsEmpty();

        h.PushSnapshot(activeAgents: 0);
        h.PushConnecting();
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the Windows service offer");
    }

    [Test]
    public async Task The_explicit_action_installs_the_windows_service_through_the_lane() {
        await using var h = new Harness();
        h.Start();

        await h.Controller.InstallWindowsServiceAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.InstallWindowsService);
    }

    /// The takeover stops the task's daemon before the service starts its own: the banner names the move,
    /// not a daemon that is down, until the service's daemon attaches
    [Test]
    public async Task Moving_to_the_windows_service_is_shown_until_the_daemon_attaches_again() {
        await using var h = new Harness();
        h.Start();
        h.PushConnected();
        await h.Controller.PhaseClosed;
        var moving = false;
        using var _ = h.Controller.IsMovingToWindowsService.Subscribe(m => moving = m);

        await h.Controller.InstallWindowsServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.StatusMessages).Contains(DaemonLifecycleController.MovingToWindowsServiceStatus);
        await Assert.That(h.Surface.Withdrawn).IsEmpty();
        await Assert.That(moving).IsTrue();

        h.PushConnecting();
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Withdrawn.Contains(DaemonLifecycleController.MovingToWindowsServiceStatus),
            what: "the moving line withdrawn");
        await Assert.That(moving).IsFalse();
    }

    [Test]
    public async Task A_failed_move_to_the_windows_service_takes_its_line_back() {
        await using var h = new Harness();
        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(
            new MutationOutcome.Failed(1, "elevation_declined", RecoverySurface.Attention));
        h.Start();

        await h.Controller.InstallWindowsServiceAsync(CancellationToken.None);

        await Assert.That(h.Surface.Withdrawn).IsEquivalentTo([DaemonLifecycleController.MovingToWindowsServiceStatus]);
    }

    sealed class Harness : IAsyncDisposable {
        public readonly FakeDaemonClientService Client = new();
        public readonly FakeKcapCli Cli = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly FakeMutationLane Lane = new();
        public readonly DaemonLifecycleController Controller;
        public readonly string? CanonicalServer;

        public string? ProfileName = "default";
        public bool NeedsAppRestart;
        public bool WindowsServiceDeclined;
        public bool OffersSupervision = true;
        public int DeclinesRemembered;

        public Harness(
                string? canonicalServer = "https://kcap.example.com:443", bool autoActionsPermanentlyClosed = false) {
            CanonicalServer = canonicalServer;
            Time  = new TimerCountingTimeProvider(Clock);
            Controller = new DaemonLifecycleController(
                Client, Cli, Probe, Surface, () => Task.FromResult<string?>(ProfileName), Time,
                CanonicalServer, Lane.RunAsync, autoActionsPermanentlyClosed, () => NeedsAppRestart,
                () => Task.FromResult(WindowsServiceDeclined),
                () => { DeclinesRemembered++; return Task.CompletedTask; }) { OffersSupervision = () => OffersSupervision };
        }

        public void Start() => Controller.Start();

        public void PushConnecting() =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));

        public void PushConnected() =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));

        public void PushUnreachable(string reason = "daemon_unreachable", string? daemonVersion = null) =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null, daemonVersion));

        public void PushSnapshot(int activeAgents) =>
            Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(daemon: "daemon-a", active: activeAgents,
                serverUrl: CanonicalServer ?? "http://localhost:9999"));

        public bool Background() { bool? v = null; using (Controller.BackgroundPriority.Subscribe(x => v = x)) { } return v ?? false; }
        public ReloadState? Reload() { ReloadState? v = null; using (Controller.ReloadState.Subscribe(x => v = x)) { } return v; }
        public bool Reloading() { bool? v = null; using (Controller.IsReloading.Subscribe(x => v = x)) { } return v ?? false; }
        public string? RepairOffer() { string? v = null; using (Controller.RepairOffer.Subscribe(x => v = x)) { } return v; }

        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }
}

// TimerCountingTimeProvider is shared from ConsentServiceTests.cs (same namespace).

/// Scripted IKcapCli — every member is a settable behavior func plus a call counter, so tests
/// can drive both immediate results and TaskCompletionSource-controlled hangs (the once-per-run
/// arm test) without touching a real process. The controller never calls StartVerified/
/// InstallVerified/DetachedStart (those go through the lane); their counters are a tripwire every
/// controller test asserts stays 0.
sealed class FakeKcapCli : IKcapCli {
    public string? CliPath { get; set; } = "/opt/kcap/bin/kcap";

    public int VersionCallCount;
    public Func<CancellationToken, Task<string?>> VersionBehavior = _ => Task.FromResult<string?>("1.0.0");
    public Task<string?> VersionAsync(CancellationToken ct) {
        VersionCallCount++;
        return VersionBehavior(ct);
    }

    public int StatusCallCount;
    public Func<CancellationToken, Task<ServiceSnapshot?>> StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
    public Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) {
        StatusCallCount++;
        return StatusBehavior(ct);
    }

    public readonly List<string> NamedStatusReads = [];
    public Func<string, Task<ServiceSnapshot?>> NamedStatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);
    public Task<ServiceSnapshot?> ServiceStatusAsync(string daemonName, CancellationToken ct) {
        NamedStatusReads.Add(daemonName);
        return NamedStatusBehavior(daemonName);
    }

    public bool SupportsRetire = true;
    public Task<bool> SupportsServiceRetireAsync(CancellationToken ct) => Task.FromResult(SupportsRetire);

    public int StartVerifiedCallCount;
    public Func<CancellationToken, Task<ProcessResult>> StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) {
        StartVerifiedCallCount++;
        return StartVerifiedBehavior(ct);
    }

    public int ReloadCallCount;
    public Func<CancellationToken, Task<ProcessResult>> ReloadBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceReloadAsync(CancellationToken ct) {
        ReloadCallCount++;
        return ReloadBehavior(ct);
    }

    public int InstallVerifiedCallCount;
    public bool? LastInstallReplace;
    public string? LastRetireServiceId;
    public Func<bool, CancellationToken, Task<ProcessResult>> InstallVerifiedBehavior =
        (_, _) => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct, string? retireServiceId = null) {
        InstallVerifiedCallCount++;
        LastInstallReplace = replace;
        LastRetireServiceId = retireServiceId;
        return InstallVerifiedBehavior(replace, ct);
    }

    public int InstallWindowsServiceCallCount;
    public Func<CancellationToken, Task<ProcessResult>> InstallWindowsServiceBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> ServiceInstallWindowsServiceAsync(CancellationToken ct) {
        InstallWindowsServiceCallCount++;
        return InstallWindowsServiceBehavior(ct);
    }

    public int DetachedStartCallCount;
    public Func<CancellationToken, Task<ProcessResult>> DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false));

    public string? LastBootAttemptId;
    public Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct) {
        DetachedStartCallCount++;
        LastBootAttemptId = bootAttemptId;
        return DetachedStartBehavior(ct);
    }

    public int PluginInstallCallCount;
    public readonly List<string?> PluginInstallCalls = []; // call-order proof for sequential-install tests
    public readonly List<IReadOnlyList<string>> PluginInstallOptions = [];
    public Func<string?, CancellationToken, Task<ProcessResult>> PluginInstallBehavior =
        (_, _) => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> PluginRemoveAsync(string? vendorFlag, CancellationToken ct) => throw new NotSupportedException();

    public Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct, IReadOnlyList<string>? options = null) {
        PluginInstallCallCount++;
        PluginInstallCalls.Add(vendorFlag);
        PluginInstallOptions.Add(options ?? []);
        return PluginInstallBehavior(vendorFlag, ct);
    }

    public readonly List<(MutationVerb Verb, AccountArgs Args)> AccountsCalls = [];
    public Func<MutationVerb, AccountArgs, CancellationToken, Task<ProcessResult>> AccountsBehavior =
        (_, _, _) => Task.FromResult(new ProcessResult(0, "", "", false));
    public Task<ProcessResult> AccountsAsync(MutationVerb verb, AccountArgs args, CancellationToken ct) {
        AccountsCalls.Add((verb, args));
        return AccountsBehavior(verb, args, ct);
    }

    public readonly List<IReadOnlyList<string>> DiscoverCalls = [];
    public Func<IReadOnlyList<string>, Task<ImportDiscoveryReport?>> DiscoverBehavior =
        _ => Task.FromResult<ImportDiscoveryReport?>(new ImportDiscoveryReport([], 0, []));
    public Task<ImportDiscoveryReport?> ImportDiscoverAsync(IReadOnlyList<string> vendorFlags, CancellationToken ct) {
        DiscoverCalls.Add(vendorFlags);
        return DiscoverBehavior(vendorFlags);
    }

    public int ImportCallCount;
    public readonly List<ImportRequest> ImportRequests = [];
    public Func<ImportRequest, Action<StreamedLine>, CancellationToken, Task<StreamingResult>> ImportBehavior =
        (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));
    public Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct) {
        ImportCallCount++;
        ImportRequests.Add(request);
        return ImportBehavior(request, onLine, ct);
    }

    public int SetupLocalCallCount;
    public Func<Action<StreamedLine>, CancellationToken, CancellationToken, Task<StreamingResult>> SetupLocalBehavior =
        (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));
    public List<LocalEmbeddingChoice> SetupLocalEmbeddings { get; } = [];
    public List<bool> SetupLocalWindowsService { get; } = [];
    public Task<StreamingResult> SetupLocalAsync(
            LocalEmbeddingChoice embeddings, Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct,
            bool windowsService = false) {
        SetupLocalCallCount++;
        SetupLocalEmbeddings.Add(embeddings);
        SetupLocalWindowsService.Add(windowsService);
        return SetupLocalBehavior(onLine, interrupt, ct);
    }

    public int LocalFetchCallCount;
    public Func<Action<StreamedLine>, CancellationToken, Task<StreamingResult>> LocalFetchBehavior =
        (_, _) => Task.FromResult(new StreamingResult(0, false, []));
    public Task<StreamingResult> LocalFetchAsync(Action<StreamedLine> onLine, CancellationToken interrupt) {
        Interlocked.Increment(ref LocalFetchCallCount);
        return LocalFetchBehavior(onLine, interrupt);
    }

    public int LocalUpgradeCallCount;
    public Func<Action<StreamedLine>, CancellationToken, CancellationToken, Task<StreamingResult>> LocalUpgradeBehavior =
        (_, _, _) => Task.FromResult(new StreamingResult(3, false, []));
    public Task<StreamingResult> LocalUpgradeAsync(Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct) {
        LocalUpgradeCallCount++;
        return LocalUpgradeBehavior(onLine, interrupt, ct);
    }

    public List<bool> UninstallPurgeFlags { get; } = [];
    public Func<Action<StreamedLine>, Task<StreamingResult>> UninstallBehavior =
        _ => Task.FromResult(new StreamingResult(1, false, []));
    public Task<StreamingResult> UninstallAsync(bool purgeLocal, Action<StreamedLine> onLine, CancellationToken ct) {
        UninstallPurgeFlags.Add(purgeLocal);
        return UninstallBehavior(onLine);
    }

    public Task<ProcessResult> LocalStartAsync(CancellationToken ct) =>
        Task.FromResult(new ProcessResult(0, "", "", false));

    public Task<Capacitor.Cli.Core.LocalEdition.LocalServerReading?> LocalStatusAsync(CancellationToken ct) =>
        Task.FromResult<Capacitor.Cli.Core.LocalEdition.LocalServerReading?>(null);
}

// FakeLoginShellProbe is shared via Capacitor.Tests.Helpers (the controller only ever calls
// TerminalPathAsync — the install precondition; KcapOnPathAsync serves the ShimOfferCoordinator
// tests, and the fresh-answer seam scripts the post-install re-probe).
