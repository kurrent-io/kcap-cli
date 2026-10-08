using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
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
    public async Task Row3_orphan_label_is_attention_only() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        h.PushUnreachable();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the orphan-label attention");
        await h.Controller.PhaseClosed;
        await Assert.That(h.Lane.Requests).IsEmpty();
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
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap()); // nothing at all — DetachedStart row
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.DetachedStart);
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

    [Test]
    public async Task Connected_first_then_unreachable_is_inert() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        h.PushConnected();
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "the reconciliation query");
        await h.Controller.PhaseClosed;

        h.PushUnreachable();
        await Task.Delay(50); // a negative: give a would-be matrix run every chance to fire

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    [Test]
    public async Task Incompatible_first_runs_reconciliation_then_unreachable_is_inert() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnMarker: true, txnActive: false));
        h.Start();

        h.PushUnreachable(reason: "daemon_incompatible");
        await WaitUntilAsync(() => h.Cli.StatusCallCount == 1, what: "reconciliation runs on the incompatible path too");
        await h.Controller.PhaseClosed;
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention it found");

        h.PushUnreachable(); // daemon_unreachable, a later terminal outcome — inert (arm already claimed)
        await Task.Delay(50);

        await Assert.That(h.Cli.StatusCallCount).IsEqualTo(1);
        await Assert.That(h.Lane.Requests).IsEmpty();
    }

    // ---- once-per-run arm ----

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
    public async Task Reconciliation_on_connected_stale_txn_marker_is_attention() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(txnMarker: true, txnActive: false));
        h.Start();

        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the stale-marker attention");
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
    // call, but the once-per-run arm still holds (no retry on a second daemon_unreachable).
    [Test]
    public async Task Failed_outcome_from_the_lane_produces_no_controller_surface_call_but_counts_the_run_once() {
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

        h.PushUnreachable(); // once-per-run: no retry
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
    public async Task StartAction_nothing_at_all_falls_back_to_detached_start() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap());
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Lane.Requests.Count).IsEqualTo(1);
        await Assert.That(h.Lane.Requests[0].Verb).IsEqualTo(MutationVerb.DetachedStart);
        // DetachedStart from Start shares the SAME success handling as every other verb.
        await Assert.That(h.Client.RestartCount).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages.Count).IsEqualTo(1);
        await Assert.That(h.Surface.StatusMessages[0]).Contains("Waiting to connect");
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
    public async Task StartAction_orphan_label_offers_repair() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: false, state: "installed"));
        h.Start();

        await h.Controller.StartActionAsync(CancellationToken.None);

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindRepair);
        await Assert.That(h.Lane.Requests).IsEmpty();
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
        await using var h = new Harness();
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
        await Assert.That(h.Surface.Prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.ReloadDisclosure(3));
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
            1 => slowRead.Task,
            2 => Task.FromResult<ServiceSnapshot?>(null),
            _ => Task.FromResult<ServiceSnapshot?>(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" }),
        };
        h.Start();
        h.PushConnected(); // the arm: read 1 is its slow reconciliation read

        h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention));
        await h.Controller.ReloadServiceAsync(CancellationToken.None);
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        slowRead.SetResult(Snap(state: "running", jobPid: 100, daemonPid: 100) with { LoadedSpawnType = "daemon" });
        await h.Controller.QuiescedAsync(); // the reconciliation holds the gate until its read has been noted
        await Assert.That(h.Reload()!.Token).IsEqualTo("contended");

        h.PushConnecting(); h.PushConnected(); // a read that starts after the record
        await WaitUntilAsync(() => h.Reload() is null, what: "the later positive read clearing the failure");
    }

    [Test]
    public async Task Disclosure_names_the_count_and_what_else_ends() {
        var text = DaemonLifecycleController.ReloadDisclosure(2);
        await Assert.That(text).IsEqualTo("Reloading restarts the daemon and ends everything it hosts: 2 agents now, plus any agent, launch or evaluation running when it exits. Uncommitted work in their worktrees is lost.");
        await Assert.That(DaemonLifecycleController.ReloadDisclosure(1)).Contains("1 agent now");
    }

    // ---- harness ----

    /// Records every MutationRequest the controller hands to `_runMutation` and lets a test
    /// script an outcome per request (blanket Behavior, TCS-friendly) — the fake lane seam Task
    /// 10's controller tests drive instead of a raw IKcapCli mutation call. Defaults to an
    /// immediate Succeeded so tests that don't care about the mutation's own outcome (most of the
    /// "no mutation happens" tests never even call this) aren't forced to script one.
    sealed class FakeMutationLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
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

        public Harness(
                string? canonicalServer = "https://kcap.example.com:443", bool autoActionsPermanentlyClosed = false) {
            CanonicalServer = canonicalServer;
            Time  = new TimerCountingTimeProvider(Clock);
            Controller = new DaemonLifecycleController(
                Client, Cli, Probe, Surface, () => Task.FromResult<string?>(ProfileName), Time,
                CanonicalServer, Lane.RunAsync, autoActionsPermanentlyClosed, () => NeedsAppRestart);
        }

        public void Start() => Controller.Start();

        public void PushConnecting() =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));

        public void PushConnected() =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));

        public void PushUnreachable(string reason = "daemon_unreachable", string? daemonVersion = null) =>
            Client.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null, daemonVersion));

        public void PushSnapshot(int activeAgents) =>
            Client.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(daemon: "daemon-a", active: activeAgents));

        public bool Background() { bool? v = null; using (Controller.BackgroundPriority.Subscribe(x => v = x)) { } return v ?? false; }
        public ReloadState? Reload() { ReloadState? v = null; using (Controller.ReloadState.Subscribe(x => v = x)) { } return v; }
        public bool Reloading() { bool? v = null; using (Controller.IsReloading.Subscribe(x => v = x)) { } return v ?? false; }

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
    public Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct, IReadOnlyList<string>? options = null) {
        PluginInstallCallCount++;
        PluginInstallCalls.Add(vendorFlag);
        PluginInstallOptions.Add(options ?? []);
        return PluginInstallBehavior(vendorFlag, ct);
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
}

// FakeLoginShellProbe is shared via Capacitor.Tests.Helpers (the controller only ever calls
// TerminalPathAsync — the install precondition; KcapOnPathAsync serves the ShimOfferCoordinator
// tests, and the fresh-answer seam scripts the post-install re-probe).
