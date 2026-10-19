using System.ComponentModel;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Every row of the daemon matrix → the exact mutation verb the enabler runs unasked (or none), the
/// one takeover that asks first, and the consent-flip claim application rules. The lane is a
/// recording delegate, the claims store is REAL on temp paths (so TryConsume's own two-lock compare
/// runs), and the surface records every dialog. Every "no claim application" assertion checks
/// GetCalls, not just PutV2Calls: the GET is the FIRST ops touch, so a guard that stopped guarding
/// would be invisible otherwise.
public class DaemonEnablerTests {
    const string Profile    = "default";
    const string RawServer  = "https://example.test";
    const string DaemonName = "kcap-daemon";
    static readonly string CanonicalServer = ServerIdentity.Canonicalize(RawServer)!;

    static ServiceSnapshot Snap(
            string state = "not_installed", bool unitPresent = false, int? jobPid = null, int? daemonPid = null,
            bool txnMarker = false, bool txnActive = false, string? installBinaryPath = "/opt/kcap/kcap-daemon") =>
        new(DaemonName, unitPresent, state, "/opt/kcap/kcap-daemon", installBinaryPath, jobPid, daemonPid,
            txnMarker, txnActive);

    static ServiceSnapshot OwnedRunning() => Snap(state: "running", unitPresent: true, jobPid: 100, daemonPid: 100);

    static ServiceSnapshot ManualRunning() => Snap(state: "not_installed", daemonPid: 777);

    static ObservedEvidence Evidence(
            string? server = RawServer, string? name = DaemonName, bool reachable = true, bool consistent = true,
            params string[] capabilities) =>
        new(reachable, capabilities.Length == 0 ? ["consent/3"] : capabilities, "0.12.0-beta.1", server, name,
            4242, "instance-1", consistent);

    static ConsentPolicyDto Policy(string @default = "allow", params ConsentRuleDto[] rules) =>
        new(@default, 300, [.. rules]);

    /// Records every request the enabler routes through the lane and answers with the scripted
    /// outcome — the lane's own result IS the enabler's success predicate.
    sealed class RecordingLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    sealed class ScriptedObservation : IDaemonObservation {
        public ObservedEvidence? Default;

        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) =>
            Task.FromResult(Default);
    }

    sealed class Harness : IDisposable {
        readonly TempConfigRoot _config = new();

        public readonly FakeKcapCli Cli = new();
        public readonly RecordingLane Lane = new();
        public readonly ScriptedObservation Observation = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero));
        public readonly TimerCountingTimeProvider Time;
        public readonly ConsentFlipClaims Claims;
        public readonly MachineNameViewModel? Name;
        public readonly DaemonEnabler Vm;

        public (string Profile, string Server, string DaemonName)? Identity = (Profile, RawServer, DaemonName);
        public (string Profile, string Server, string DaemonName) UnderConfigLock = (Profile, RawServer, DaemonName);
        public string? TerminalPath = "/usr/bin:/bin";
        public Func<CancellationToken, Task<string?>> Pick = _ => Task.FromResult<string?>(null);
        public int PickCalls;

        public Harness(bool withName = false) {
            Time   = new TimerCountingTimeProvider(Clock);
            Claims = new ConsentFlipClaims(_config.Root);
            Name   = withName ? new MachineNameViewModel(_config.Root, "first-name") : null;
            Vm = new DaemonEnabler(
                Cli, Lane.RunAsync, () => Identity, Observation, Ops, Claims, () => UnderConfigLock, Surface,
                _ => Task.FromResult<string?>(TerminalPath), Time, Name,
                ct => {
                    PickCalls++;
                    return Pick(ct);
                });
        }

        public ConfigRoot Root => _config.Root;

        public string? SavedName() =>
            File.Exists(AppConfig.GetConfigPath(Root))
                ? ConfigMutator.LoadPure(AppConfig.GetConfigPath(Root)).Profiles.Values.SingleOrDefault()?.Daemon?.Name
                : null;

        public void ArmClaim() => Claims.Arm(new ConsentFlipClaim(Profile, CanonicalServer));

        public void Status(ServiceSnapshot? snapshot) => Cli.StatusBehavior = _ => Task.FromResult(snapshot);

        public Task Ensure(CancellationToken ct = default) => Vm.EnsureAsync(ct);

        public void Dispose() => _config.Dispose();
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    /// The wizard handoff waits on this: a status read is not lane work, so the lane alone looks idle.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Quiesced_waits_for_an_ensure_still_reading_status() {
        var (beforeRead, afterRead) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var status = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.StatusBehavior = _ => status.Task;

            var ensure   = h.Ensure();
            var quiesced = h.Vm.QuiescedAsync();
            await Task.Delay(50); // a negative: give a premature quiesce every chance to complete
            var beforeRead = quiesced.IsCompleted;

            status.SetResult(Snap(state: "some_future_state"));
            await ensure;
            await quiesced;

            return (beforeRead, quiesced.IsCompleted);
        });

        await Assert.That(beforeRead).IsFalse();
        await Assert.That(afterRead).IsTrue();
    }

    // ── gate / precondition rows ────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Real_async_status_and_mutation_results_update_the_enabler_on_the_UI_thread() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var status   = new TaskCompletionSource<ServiceSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutation = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.StatusBehavior = _ => status.Task;
            h.Lane.Behavior = (_, _) => mutation.Task;
            var wrongThread = false;
            h.Vm.PropertyChanged += (_, _) => wrongThread |= !Dispatcher.UIThread.CheckAccess();

            var ensure = h.Ensure();
            await Task.Run(() => status.SetResult(Snap()));
            await WaitUntilAsync(() => h.Lane.Requests.Count == 1, "the install to reach the lane");
            await Task.Run(() => mutation.SetResult(new MutationOutcome.Succeeded()));
            await ensure;

            await Assert.That(wrongThread).IsFalse();
            await Assert.That(h.Vm.Satisfied).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_committed_sign_in_nothing_is_read_or_run() {
        var (row, message, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Identity = null;

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.RequiresSignIn);
        await Assert.That(message).IsEqualTo(DaemonEnabler.RequiresSignInMessage);
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_resolved_CLI_nothing_is_read_or_run() {
        var (row, message, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Cli.CliPath = null;

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.CliMissing);
        await Assert.That(message).IsEqualTo(DaemonEnabler.CliMissingMessage);
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    /// A PATH lookup that finds nothing throws from the spawn. That is not an unreadable service.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_CLI_the_process_cannot_start_is_missing() {
        var (row, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Cli.StatusBehavior = _ => throw new Win32Exception(2);

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.CliMissing);
        await Assert.That(message).IsEqualTo(DaemonEnabler.CliMissingMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_server_that_cannot_be_canonicalized_refuses_before_any_status_read() {
        var (row, message, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Identity = (Profile, "file:///etc/passwd", DaemonName);

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NoServerConfigured);
        await Assert.That(message).IsEqualTo(DaemonEnabler.NoServerMessage); // humanized, not the raw token
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ensure_after_sign_in_commits_runs_on_the_now_resolvable_identity() {
        var (firstRow, secondRow, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Identity = null;

            await h.Ensure();
            var first = h.Vm.Row;

            h.Identity = (Profile, RawServer, DaemonName);
            await h.Ensure();

            return (first, h.Vm.Row, h.Lane.Requests.ToList());
        });

        await Assert.That(firstRow).IsEqualTo(DaemonRow.RequiresSignIn);
        await Assert.That(secondRow).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Install);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unresolvable_daemon_binary_runs_no_unit_writing_mutation() {
        var (row, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(installBinaryPath: null));

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.BinaryUnresolved);
        await Assert.That(message).IsEqualTo(DaemonEnabler.BinaryUnresolvedMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unresolvable_daemon_binary_still_starts_an_installed_unit() {
        var (row, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "installed", unitPresent: true, installBinaryPath: null));

            await h.Ensure();

            return (h.Vm.Row, h.Lane.Requests.ToList());
        });

        await Assert.That(row).IsEqualTo(DaemonRow.Stopped); // starting an installed unit writes none
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    // ── unreadable / unrecognized evidence: honest message, no mutation ─────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreadable_status_is_an_honest_message_with_no_mutation() {
        var (row, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(null);

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.StatusUnknown);
        await Assert.That(message).IsEqualTo(DaemonEnabler.StatusUnknownMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unrecognized_service_state_never_reads_as_not_installed() {
        var (row, message, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "some_future_state"));

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.StatusUnknown);
        await Assert.That(message).IsEqualTo(DaemonEnabler.UnrecognizedStateMessage);
        await Assert.That(mutations).IsEqualTo(0);
    }

    // ── install / start rows ────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_unit_and_no_live_daemon_installs_through_the_lane_without_asking() {
        var (row, requests, prompts, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());

            await h.Ensure();

            return (h.Vm.Row, h.Lane.Requests.ToList(), h.Surface.Prompts.Count, h.Vm.Satisfied);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(requests[0].Profile).IsEqualTo(Profile);
        await Assert.That(requests[0].CanonicalServer).IsEqualTo(CanonicalServer);
        await Assert.That(requests[0].DaemonName).IsEqualTo(DaemonName);
        await Assert.That(prompts).IsEqualTo(0);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    [Arguments("installed")]    // loaded label, inactive job
    [Arguments("not_installed")] // unit on disk, no loaded label
    [NotInParallel("AvaloniaSession")]
    public async Task A_present_unit_with_no_live_daemon_gets_a_verified_start(string state) {
        var (row, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: state, unitPresent: true));

            await h.Ensure();

            return (h.Vm.Row, h.Lane.Requests.ToList());
        });

        await Assert.That(row).IsEqualTo(DaemonRow.Stopped);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.StartVerified);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_job_with_no_daemon_evidence_runs_nothing() {
        var (row, mutations, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(state: "running", unitPresent: true, jobPid: 100));

            await h.Ensure();

            return (h.Vm.Row, h.Lane.Requests.Count, h.Vm.Satisfied);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.RunningUnconfirmed);
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(satisfied).IsFalse();
    }

    // ── ownership rows ──────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Positive_ownership_with_an_identity_match_is_already_enabled_and_applies_the_claim() {
        var (row, satisfied, mutations, putPayloads, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Satisfied, h.Lane.Requests.Count, h.Ops.PutV2Payloads.ToList(),
                h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.AlreadyEnabled);
        await Assert.That(satisfied).IsTrue();
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(putPayloads.Count).IsEqualTo(1);
        await Assert.That(putPayloads[0].ExpectedName).IsEqualTo(DaemonName);
        await Assert.That(putPayloads[0].ExpectedServerUrl).IsEqualTo(CanonicalServer);
        await Assert.That(putPayloads[0].Policy.Default).IsEqualTo("prompt");
        await Assert.That(pending).IsEqualTo(0); // consumed through the real two-lock clear
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_claim_put_preserves_the_daemons_existing_rules() {
        var payload = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy("allow", new ConsentRuleDto("deny", "someone", null, null, null)));
            h.Ops.QueuePutV2(true, null);

            await h.Ensure();

            return h.Ops.PutV2Payloads.Single();
        });

        await Assert.That(payload.Policy.Rules.Count).IsEqualTo(1);
        await Assert.That(payload.Policy.PromptTimeoutSeconds).IsEqualTo(300);
    }

    /// The service belongs to another server or profile: the one question the enabler asks.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Declining_to_replace_a_service_held_by_another_identity_mutates_nothing_and_applies_no_claim() {
        var (row, prompts, status, satisfied, mutations, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence(server: "https://other.test");

            await h.Ensure(); // the surface declines by default

            return (h.Vm.Row, h.Surface.Prompts.ToList(), h.Vm.Status, h.Vm.Satisfied, h.Lane.Requests.Count,
                h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.OwnedIdentityMismatch);
        await Assert.That(prompts.Count).IsEqualTo(1);
        await Assert.That(prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(prompts[0].Disclosure).IsEqualTo(DaemonLifecycleController.TakeoverDisclosure);
        await Assert.That(prompts[0].PathDegraded).IsFalse();
        await Assert.That(status).IsEqualTo(DaemonEnabler.TakeoverDeclinedMessage);
        await Assert.That(satisfied).IsFalse();
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(gets).IsEqualTo(0); // the claim path was never entered at all
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accepting_the_replacement_of_a_service_held_by_another_identity_runs_Replace() {
        var (prompts, requests, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence(server: "https://other.test");
            h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

            await h.Ensure();

            return (h.Surface.Prompts.Count, h.Lane.Requests.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(prompts).IsEqualTo(1);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unknown_terminal_PATH_is_disclosed_on_the_replacement_prompt() {
        var degraded = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.TerminalPath = null;
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence(server: "https://other.test");

            await h.Ensure();

            return h.Surface.Prompts.Single().PathDegraded;
        });

        await Assert.That(degraded).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unverifiable_daemon_identity_fails_closed_to_the_asked_replacement() {
        var (row, prompts, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence(consistent: false); // the two probe dials disagree

            await h.Ensure();

            return (h.Vm.Row, h.Surface.Prompts.Count, h.Lane.Requests.Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.OwnedIdentityMismatch);
        await Assert.That(prompts).IsEqualTo(1);
        await Assert.That(mutations).IsEqualTo(0);
    }

    // ── manual-daemon rows ──────────────────────────────────────────────────

    /// The same identity outside the service changes no one else's daemon, so it is adopted unasked.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_identity_matched_manual_daemon_is_replaced_without_asking_and_gets_the_claim() {
        var (row, prompts, requests, satisfied, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(ManualRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Ensure();

            return (h.Vm.Row, h.Surface.Prompts.Count, h.Lane.Requests.ToList(), h.Vm.Satisfied,
                h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.ManualIdentityMatch);
        await Assert.That(prompts).IsEqualTo(0);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(satisfied).IsTrue();
        await Assert.That(pending).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Declining_to_replace_a_manual_daemon_for_another_server_mutates_nothing_and_applies_no_claim() {
        var (row, message, prompts, status, mutations, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(ManualRunning());
            h.Observation.Default = Evidence(server: "https://other.test");

            await h.Ensure();

            return (h.Vm.Row, h.Vm.Message, h.Surface.Prompts.ToList(), h.Vm.Status, h.Lane.Requests.Count,
                h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.ManualIdentityMismatch);
        await Assert.That(message).Contains("https://other.test");
        await Assert.That(prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindTakeover);
        await Assert.That(status).IsEqualTo(DaemonEnabler.TakeoverDeclinedMessage);
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(gets).IsEqualTo(0);
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Accepting_the_replacement_of_a_manual_daemon_for_another_server_runs_Replace() {
        var (prompts, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(ManualRunning());
            h.Observation.Default = Evidence(server: "https://other.test");
            h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);

            await h.Ensure();

            return (h.Surface.Prompts.Count, h.Lane.Requests.ToList());
        });

        await Assert.That(prompts).IsEqualTo(1);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
    }

    // ── repair rows ─────────────────────────────────────────────────────────

    /// Our own broken service is repaired unasked; a stale marker is never a blind reinstall.
    [Test]
    [Arguments(true)]  // orphan label: loaded, unit file missing
    [Arguments(false)] // stale transaction marker
    [NotInParallel("AvaloniaSession")]
    public async Task A_repair_row_runs_Replace_without_asking(bool orphanLabel) {
        var (row, prompts, requests) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(orphanLabel ? Snap(state: "installed", unitPresent: false) : Snap(txnMarker: true));

            await h.Ensure();

            return (h.Vm.Row, h.Surface.Prompts.Count, h.Lane.Requests.ToList());
        });

        await Assert.That(row).IsEqualTo(orphanLabel ? DaemonRow.OrphanLabel : DaemonRow.StaleMarker);
        await Assert.That(prompts).IsEqualTo(0);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Replace);
    }

    // ── txn_active: wait, never a parallel mutation ─────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_transaction_is_waited_out_and_the_matrix_proceeds_once_it_clears() {
        var (row, requests, statusCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var call = 0;
            h.Cli.StatusBehavior = _ => {
                call++;
                return Task.FromResult<ServiceSnapshot?>(call == 1 ? Snap(txnActive: true) : Snap());
            };

            var ensure = h.Ensure();
            await WaitUntilAsync(() => h.Time.TimersCreated >= 1, "the txn-active poll timer to be armed");
            await Assert.That(h.Lane.Requests).IsEmpty(); // never mutated into the held flock
            h.Clock.Advance(DaemonEnabler.TxnPollInterval);
            await ensure;

            return (h.Vm.Row, h.Lane.Requests.ToList(), h.Cli.StatusCallCount);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.NotInstalled);
        await Assert.That(requests.Single().Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(statusCalls).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_transaction_that_never_clears_ends_in_an_honest_message_with_no_mutation() {
        var (row, message, mutations, statusCalls) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap(txnActive: true));

            var ensure = h.Ensure();
            for (var poll = 0; poll < DaemonEnabler.MaxTxnPolls && !ensure.IsCompleted; poll++) {
                await WaitUntilAsync(() => h.Time.TimersCreated >= poll + 1, "the next txn-active poll timer");
                h.Clock.Advance(DaemonEnabler.TxnPollInterval);
            }
            await ensure;

            return (h.Vm.Row, h.Vm.Message, h.Lane.Requests.Count, h.Cli.StatusCallCount);
        });

        await Assert.That(row).IsEqualTo(DaemonRow.TransactionActive);
        await Assert.That(message).IsEqualTo(DaemonEnabler.TxnActiveMessage);
        await Assert.That(mutations).IsEqualTo(0);
        await Assert.That(statusCalls).IsEqualTo(DaemonEnabler.MaxTxnPolls + 1);
    }

    // ── outcome handling: state only, never a second presentation ───────────

    [Test]
    [Arguments("succeeded", true)]
    [Arguments("succeeded_after_timeout", true)]
    [Arguments("unconfirmed", false)]
    [Arguments("attention_skew", false)]
    [Arguments("attention_repair", false)]
    [Arguments("refused", false)]
    [Arguments("failed", false)]
    [NotInParallel("AvaloniaSession")]
    public async Task Satisfied_follows_the_lanes_own_outcome_and_nothing_else(string outcomeKey, bool expected) {
        var (satisfied, status, prompts, statusLines, attentionLines) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(outcomeKey switch {
                "succeeded"               => new MutationOutcome.Succeeded(),
                "succeeded_after_timeout" => new MutationOutcome.SucceededAfterTimeout(),
                "unconfirmed"             => new MutationOutcome.UnconfirmedNoAttach(),
                "attention_skew"          => new MutationOutcome.AttentionSkew("daemon_below_floor"),
                "attention_repair"        => new MutationOutcome.AttentionRepair("stale_marker"),
                "refused"                 => new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention),
                _                         => new MutationOutcome.Failed(28, "identity_mismatch", RecoverySurface.Takeover),
            });

            await h.Ensure();

            return (h.Vm.Satisfied, h.Vm.Status, h.Surface.Prompts.Count, h.Surface.StatusMessages.Count,
                h.Surface.AttentionMessages.Count);
        });

        await Assert.That(satisfied).IsEqualTo(expected);
        await Assert.That(status).IsNotNull();
        // Single presentation: the waiter never opens a dialog and never touches the surface —
        // actionable outcomes reach the user through the channel consumer instead.
        await Assert.That(prompts).IsEqualTo(0);
        await Assert.That(statusLines).IsEqualTo(0);
        await Assert.That(attentionLines).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_outcome_names_its_coded_reason() {
        var status = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            h.Lane.Behavior = (_, _) =>
                Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(24, null, RecoverySurface.Attention));

            await h.Ensure();

            return h.Vm.Status;
        });

        await Assert.That(status).Contains("verify_readiness_timeout");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ensure_after_success_reads_and_runs_nothing() {
        var (statusBefore, statusAfter, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());

            await h.Ensure();
            var before = h.Cli.StatusCallCount;
            await h.Ensure();

            return (before, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(statusAfter).IsEqualTo(statusBefore);
        await Assert.That(mutations).IsEqualTo(1);
    }

    /// A second call while one runs waits it out, then finds the daemon enabled.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Concurrent_ensures_run_one_mutation() {
        var mutations = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Status(Snap());
            var release = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Lane.Behavior = (_, _) => release.Task;

            var first  = h.Ensure();
            await WaitUntilAsync(() => h.Lane.Requests.Count == 1, "the first install to reach the lane");
            var second = h.Ensure();
            release.SetResult(new MutationOutcome.Succeeded());
            await Task.WhenAll(first, second);

            return h.Lane.Requests.Count;
        });

        await Assert.That(mutations).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_cancelled_ensure_detaches_the_waiter() {
        var (observedCancel, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            using var cts = new CancellationTokenSource();
            h.Status(Snap());
            var laneToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The lane owns the child: only the WAITER's token is cancelled here. Read the token itself:
            // a second registration on it can be disposed by the delay's unwind before its callback runs.
            h.Lane.Behavior = async (_, ct) => {
                laneToken.TrySetResult(ct);
                await Task.Delay(Timeout.Infinite, ct);
                return new MutationOutcome.Succeeded();
            };

            var ensure = h.Ensure(cts.Token);
            var token  = await laneToken.Task;
            await cts.CancelAsync();
            await ensure;

            return (token.IsCancellationRequested, h.Vm.Status);
        });

        await Assert.That(observedCancel).IsTrue();
        await Assert.That(status).IsEqualTo(DaemonEnabler.DetachedMessage);
    }

    // ── claim application after a mutation ──────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_install_applies_the_pending_claim_against_the_daemon_it_just_verified() {
        var (putPayloads, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(true, null);

            await h.Ensure();

            return (h.Ops.PutV2Payloads.ToList(), h.Claims.Pending().Count);
        });

        await Assert.That(putPayloads.Count).IsEqualTo(1);
        await Assert.That(putPayloads[0].ExpectedName).IsEqualTo(DaemonName);
        await Assert.That(pending).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_daemon_without_consent_3_gets_no_put_and_keeps_the_claim_pending() {
        var (gets, puts, pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence(capabilities: ["consent/1"]);

            await h.Ensure();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(gets).IsEqualTo(0);
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonEnabler.ClaimMissingCapabilityMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rejected_ack_retains_the_claim_and_surfaces_repair_guidance() {
        var (pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2(false, "identity_mismatch");

            await h.Ensure();

            return (h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonEnabler.ClaimFailedMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ops_failure_retains_the_claim() {
        var (pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            h.Ops.QueuePutV2Failure("daemon_unreachable");

            await h.Ensure();

            return (h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(pending).IsEqualTo(1);
        await Assert.That(status).IsEqualTo(DaemonEnabler.ClaimFailedMessage);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_claim_for_another_server_is_never_applied() {
        var (gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Claims.Arm(new ConsentFlipClaim(Profile, ServerIdentity.Canonicalize("https://other.test")!));
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();

            await h.Ensure();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(gets).IsEqualTo(0); // the pending filter, not a later rejection, is what stops this
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_successful_mutation_whose_re_probe_no_longer_matches_applies_no_claim() {
        var (satisfied, gets, puts, pending) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(Snap());
            h.Observation.Default = Evidence(server: "https://other.test"); // the post-mutation re-check fails

            await h.Ensure();

            return (h.Vm.Satisfied, h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count);
        });

        await Assert.That(satisfied).IsTrue(); // the lane's own outcome still decides enablement
        await Assert.That(gets).IsEqualTo(0);  // but the claim never reaches a daemon we did not just verify
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_operator_chosen_deny_is_left_stricter_than_prompt() {
        var (gets, puts, pending, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy("deny"));

            await h.Ensure();

            return (h.Ops.GetCalls, h.Ops.PutV2Calls, h.Claims.Pending().Count, h.Vm.Status);
        });

        await Assert.That(gets).IsEqualTo(1);
        await Assert.That(puts).IsEqualTo(0);
        await Assert.That(pending).IsEqualTo(1); // retained, inert
        await Assert.That(status).IsEqualTo(DaemonEnabler.ClaimAlreadyStricterMessage);
    }

    /// The claim put runs untokened: cancelling the ensure while it is in flight still lets it land.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelling_the_ensure_awaits_an_in_flight_claim_put() {
        var (pending, puts) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            using var cts = new CancellationTokenSource();
            h.ArmClaim();
            h.Status(OwnedRunning());
            h.Observation.Default = Evidence();
            h.Ops.QueueGet(Policy());
            var put = h.Ops.ArmPutV2();

            var ensure = h.Ensure(cts.Token);
            await WaitUntilAsync(() => h.Ops.PutV2Calls == 1, "the conditional put to reach the ops layer");

            await cts.CancelAsync();
            put.SetResult(new ConsentAckDto(true, null, null));
            await ensure;

            return (h.Claims.Pending().Count, h.Ops.PutV2Calls);
        });

        await Assert.That(pending).IsEqualTo(0); // the put completed and the claim was consumed
        await Assert.That(puts).IsEqualTo(1);
    }

    // ── the daemon's name ───────────────────────────────────────────────────

    /// The service is installed under the picked name, so the name is on disk before the status read.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_name_is_saved_before_the_status_read() {
        var (savedAtRead, installs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness(withName: true);
            h.Pick = _ => Task.FromResult<string?>("renamed");
            string? atRead = null;
            h.Cli.StatusBehavior = _ => {
                atRead = h.SavedName();
                return Task.FromResult<ServiceSnapshot?>(Snap());
            };

            await h.Ensure();

            return (atRead, h.Lane.Requests.Count);
        });

        await Assert.That(savedAtRead).IsEqualTo("renamed");
        await Assert.That(installs).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_null_pick_keeps_the_configured_name() {
        var (pickCalls, saved, savedName, installs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness(withName: true);
            h.Status(Snap());

            await h.Ensure();

            return (h.PickCalls, h.Name!.Saved, h.SavedName(), h.Lane.Requests.Count);
        });

        await Assert.That(pickCalls).IsEqualTo(1);
        await Assert.That(saved).IsFalse();
        await Assert.That(savedName).IsNull();
        await Assert.That(installs).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_name_that_cannot_be_saved_stops_before_the_status_read() {
        var (status, machineMessage, statusCalls, mutations) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness(withName: true);
            h.Pick = _ => Task.FromResult<string?>(" ");
            h.Status(Snap());

            await h.Ensure();

            return (h.Vm.Status, h.Name!.Message, h.Cli.StatusCallCount, h.Lane.Requests.Count);
        });

        await Assert.That(machineMessage).IsNotNull();
        await Assert.That(status).IsEqualTo(machineMessage);
        await Assert.That(statusCalls).IsEqualTo(0);
        await Assert.That(mutations).IsEqualTo(0);
    }

    /// Renames the configured "old-mac" to the identity's name, over a new name with no service.
    static async Task<Harness> RenamedHarnessAsync(ServiceSnapshot? oldService) {
        var h = new Harness(withName: true);
        h.Name!.DaemonName = "old-mac";
        await h.Name.SaveAsync(CancellationToken.None);
        h.Pick = _ => Task.FromResult<string?>(DaemonName);
        h.Status(Snap());
        h.Cli.NamedStatusBehavior = _ => Task.FromResult(oldService);
        return h;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rename_retires_the_old_name_s_service_with_the_install() {
        var (requests, statusReads, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(OwnedRunning());

            await h.Ensure();

            return (h.Lane.Requests.ToList(), h.Cli.NamedStatusReads.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(statusReads).IsEquivalentTo(new[] { "old-mac" });
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(requests[0].RetireServiceId).IsEqualTo("old-mac");
        await Assert.That(satisfied).IsTrue();
    }

    /// A name with no service — never installed, or run by hand — has nothing to retire.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rename_with_no_old_service_installs_without_retiring() {
        var requests = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(Snap(state: "not_installed", daemonPid: 777));

            await h.Ensure();

            return h.Lane.Requests.ToList();
        });

        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(requests[0].RetireServiceId).IsNull();
    }

    /// An unreadable status says nothing about the old service, so the retire is still attempted; the
    /// engine skips an absent unit and refuses one it cannot prove is this profile's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rename_whose_old_status_cannot_be_read_still_retires() {
        var requests = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(oldService: null);

            await h.Ensure();

            return h.Lane.Requests.ToList();
        });

        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(requests[0].RetireServiceId).IsEqualTo("old-mac");
    }

    /// The lane fails a retire an old CLI cannot run, which would leave the new name without a daemon.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_cli_that_cannot_retire_installs_without_retiring() {
        var requests = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(OwnedRunning());
            h.Cli.SupportsRetire = false;

            await h.Ensure();

            return h.Lane.Requests.ToList();
        });

        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(requests[0].RetireServiceId).IsNull();
    }

    /// A refused retire, or one whose old service was locked by another transaction, changed nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(VerifyExitCodes.RetireRefused)]
    [Arguments(VerifyExitCodes.Contended)]
    public async Task A_retire_that_changed_nothing_installs_the_new_name_without_it(int exitCode) {
        var (requests, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(OwnedRunning());
            h.Lane.Behavior = (request, _) => Task.FromResult<MutationOutcome>(request.RetireServiceId is null
                ? new MutationOutcome.Succeeded()
                : new MutationOutcome.Failed(exitCode, "refused", RecoverySurface.Attention));

            await h.Ensure();

            return (h.Lane.Requests.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[0].RetireServiceId).IsEqualTo("old-mac");
        await Assert.That(requests[1].Verb).IsEqualTo(MutationVerb.Install);
        await Assert.That(requests[1].RetireServiceId).IsNull();
        await Assert.That(satisfied).IsTrue();
    }

    /// A failure that is not a refused retire may have changed nothing, so the retry still retires the
    /// old service.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_after_an_unrelated_failure_still_retires_the_old_service() {
        var requests = await AvaloniaSession.DispatchAsync(async () => {
            using var h = await RenamedHarnessAsync(OwnedRunning());
            h.Lane.Behavior = (_, _) => Task.FromResult<MutationOutcome>(h.Lane.Requests.Count == 1
                ? new MutationOutcome.Failed(VerifyExitCodes.ReadinessTimeout, "verify_readiness_timeout", RecoverySurface.Attention)
                : new MutationOutcome.Succeeded());

            await h.Ensure();
            await h.Ensure();

            return h.Lane.Requests.ToList();
        });

        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[0].RetireServiceId).IsEqualTo("old-mac");
        await Assert.That(requests[1].RetireServiceId).IsEqualTo("old-mac");
    }

    /// Only a name the save replaced is retired: the profile's first name has no predecessor.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_first_name_reads_no_other_service() {
        var (requests, statusReads) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness(withName: true);
            h.Pick = _ => Task.FromResult<string?>(DaemonName);
            h.Status(Snap());

            await h.Ensure();

            return (h.Lane.Requests.ToList(), h.Cli.NamedStatusReads.ToList());
        });

        await Assert.That(statusReads).IsEmpty();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RetireServiceId).IsNull();
    }
}
