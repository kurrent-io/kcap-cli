using System.Runtime.CompilerServices;
using Capacitor.Cli.Core.Telemetry;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Shared fixtures for the wizard-first startup composition. Kept out of the test classes so the
/// graph harness, the abandon cases and the rendering tests all compose the
/// wizard the way App.RunWizardModeAsync does.
static class WizardFixtures {
    internal sealed class NoopProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            throw new NotSupportedException("the wizard composition must not spawn a process");

        public Task<StreamingResult> RunStreamingAsync(
                string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotSupportedException("the wizard composition must not spawn a process");
    }

    internal sealed class NoopAppStateStore : IAppStateStore {
        public Task<AppState> LoadAsync() => Task.FromResult(new AppState());
        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(true);
    }

    internal sealed class NoopUrlOpener : IUrlOpener {
        public readonly List<string> Opened = [];
        public void Open(string url) => Opened.Add(url);
    }

    internal sealed class NeverObservation : IDaemonObservation {
        public int Calls;

        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
            Calls++;
            return Task.FromResult<ObservedEvidence?>(null);
        }
    }

    /// A fixed-answer observation, for driving the daemon enabler past ClassifyLiveDaemonAsync's
    /// owned+matched branch (the only path that actually dials the ops factory).
    internal sealed class FixedObservation(ObservedEvidence? evidence) : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) =>
            Task.FromResult(evidence);
    }

    /// Records every request the wizard routes through the lane — the "zero service mutation"
    /// assertions read this, never a status snapshot.
    internal sealed class RecordingLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    internal static Task<MutationOutcome> NeverRunMutation(MutationRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("runMutation must not be called");

    internal static Func<CancellationToken, Task<string?>> FixedTerminalPath(string? path) => _ => Task.FromResult(path);

    internal static OutcomeEnvelope Envelope(string token) =>
        new(new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a"),
            new MutationOutcome.Failed(1, token, RecoverySurface.Attention));

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    /// The whole wizard graph over fakes: every daemon touchpoint is a counting factory, so "the
    /// wizard built no daemon graph" and "the daemon enabler's ops/CLI are late-bound" are both
    /// directly observable.
    internal sealed class GraphHarness : IDisposable {
        readonly TempConfigRoot _config = new();
        public ConfigRoot ClaimsRoot => _config.Root;

        // Only the claims store is isolated into ClaimsRoot; the wizard's config reads go where WriteConfig writes.
        public readonly ConfigRoot Root;
        public readonly RecordingLane Lane = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakeKcapCli Cli = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly NeverObservation Observation = new();
        public readonly NoopUrlOpener UrlOpener = new();
        public readonly ConsentFlipClaims Claims;
        public readonly WizardBridges Bridges;
        public readonly WizardLifecycleSurface Surface;
        public readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
        public readonly WorkOSClient Workos = new(new PlainHttpClientFactory(), TimeProvider.System);
        public readonly IHttpClientFactory HttpFactory = new PlainHttpClientFactory();
        public readonly IAuthProxyClient Proxy = new AuthProxyClient(new HttpClient(), TimeProvider.System);
        public readonly List<LifecyclePrompt> Prompts = [];

        public int CliFactoryCalls;
        public int OpsFactoryCalls;
        public int DetectionFactoryCalls;
        public int DetectCalls;

        public WizardFacadeSpec? Spec;
        public Func<ConnectIntent, CancellationToken, Task<AuthResult>> Operation =
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());

        // The profile a wizard sign-in targets. Separate from Identity's, which is the daemon-facing
        // tuple; a test that writes a config naming another profile sets this too.
        public string Profile = ProfileConfig.DefaultName;

        public (string Profile, string Server, string DaemonName)? Identity = ("default", "https://acme.example", "daemon-a");
        // The claims/TryConsume path's identity (ResolveConsentFlipIdentity in production), kept
        // separate from Identity above — same default tuple, so tests that never diverge them agree.
        public (string Profile, string Server, string DaemonName) ConsentFlipIdentity = ("default", "https://acme.example", "daemon-a");
        public readonly List<string> OpsFactoryNames = [];
        public bool ShimApplicable = true;
        public string? CliPath = "/opt/kcap/bin/kcap";
        public string? ShimTarget = "/opt/kcap/bin/kcap";
        public ICliPathInstaller? ShimInstaller;
        public IReadOnlySet<HarnessId> Detected = VendorDetection.Build("claude");
        public TimeProvider Time = TimeProvider.System;
        // Matches DefaultDaemonName, so a sign-in keeps the name and writes no config of its own.
        public Func<string?> ConfiguredDaemonName = () => "daemon-a";
        public LocalRoot LocalRoot;
        public bool LocalEditionSupported = true;
        public string? ProfileOverride;

        public GraphHarness(ConfigRoot root) {
            Root      = root;
            LocalRoot = new LocalRoot(_config.PathTo("local"));
            Claims  = new ConsentFlipClaims(_config.Root);
            Bridges = WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);
            Surface = new WizardLifecycleSurface((prompt, _) => {
                Prompts.Add(prompt);
                return Task.FromResult(false);
            }, action => action());
        }

        public WizardGraphOptions Options() => new(
            Root,
            AuthFixtures.NewTokenStore(Root),
            HttpFactory,
            Proxy,
            Github,
            Workos,
            Profile,
            Claims,
            Bridges,
            spec => {
                Spec = spec;
                return Operation;
            },
            Surface,
            ResolveCli: () => {
                CliFactoryCalls++;
                return Cli;
            },
            ResolveOps: name => {
                OpsFactoryCalls++;
                OpsFactoryNames.Add(name);
                return Ops;
            },
            ResolveIdentity: () => Identity,
            ResolveConsentFlipIdentity: () => ConsentFlipIdentity,
            RunMutation: Lane.RunAsync,
            Observation: Observation,
            AppState: new NoopAppStateStore(),
            ShimInstaller: ShimInstaller ?? new PathShimInstaller(new NoopProcessRunner(), Probe),
            UrlOpener: UrlOpener,
            Probe: Probe,
            // A NEW feed per call: a composition that built one per step is caught by the factory
            // count below, and the shared instance's own call count proves both steps used it.
            DetectionFeed: _ => {
                DetectionFactoryCalls++;
                return _ => {
                    Interlocked.Increment(ref DetectCalls);
                    return Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
                        Detected.ToDictionary(id => id, _ => new DetectedAgent(true, false)));
                };
            },
            BuiltinOffer: _ => Task.FromResult<LocalEmbeddingModelManifest?>(null),
            CliPath: CliPath,
            ShimApplicable: ShimApplicable,
            ShimTarget: ShimTarget,
            DefaultDaemonName: "daemon-a",
            Time: Time,
            ShutdownToken: CancellationToken.None,
            LocalRoot: LocalRoot,
            LocalEditionSupported: LocalEditionSupported,
            ConfiguredDaemonName: ConfiguredDaemonName,
            ProfileOverride: ProfileOverride);

        public void Dispose() => _config.Dispose();
    }
}

/// <summary>
/// The wizard-first startup: the wizard composition (no daemon graph at all) and the close
/// boundary — cancel + await the sign-in's terminal answer, wait the lane out under the cap, then
/// hand the outcome channel over. StartAsync itself needs a real daemon/profile (same reason
/// AppStartupTests drives extracted statics), so this drives the seams it is composed from.
///
/// [NotInParallel]: the step ViewModels are ReactiveObjects whose WhenAnyValue wiring needs the
/// process-global headless session's ReactiveUI initialization, so every test here runs inside it.
/// </summary>
[NotInParallel("AvaloniaSession")]
public class WizardStartupTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static readonly TimeSpan Cap = TimeSpan.FromSeconds(5);

    // ── the close boundary (steps 1-4) ────────────────────────────────────────

    [Test]
    public async Task Handoff_cancels_a_pre_boundary_sign_in_and_ends_it_as_cancelled() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, null, []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Discover());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.HandoffAfterWizardAsync(graph.Auth, graph.Install, () => Task.CompletedTask, Cap, new OutcomeChannel(), TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();
            // Nothing durable: a pre-boundary cancel never armed a claim and never touched the lane.
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();

            return true;
        });
    }

    [Test]
    public async Task Handoff_waits_for_a_post_boundary_sign_in_to_answer_committed_before_transferring() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            // Past the commit boundary the façade ignores the cancel and still publishes.
            harness.Operation = async (_, _) => {
                entered.TrySetResult();
                await release.Task;
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, "someone", []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var channel = new OutcomeChannel();
            using var wizardCts = new CancellationTokenSource();
            var wizardConsumer = Task.Run(() => AppUnderTest.ConsumeMutationOutcomesAsync(
                channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
                WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, wizardCts.Token));

            var handoff = AppUnderTest.HandoffAfterWizardAsync(graph.Auth, graph.Install, () => Task.CompletedTask, Cap, channel, TimeProvider.System);
            await Task.Delay(50);

            await Assert.That(handoff.IsCompleted).IsFalse(); // the graph build waits on the terminal answer
            // Still owned by the wizard consumer: the transfer is the LAST step of the handoff.
            Assert.Throws<InvalidOperationException>(() => { _ = channel.ConsumeAsync(CancellationToken.None); });

            release.SetResult();
            await handoff.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Committed>();
            _ = channel.ConsumeAsync(CancellationToken.None); // transferred: a fresh consumer is admitted

            await wizardCts.CancelAsync();
            await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        });
    }

    /// Past the cap the handoff proceeds, but it must SAY the lane is still live —
    /// otherwise the graph comes up driving automatic actions against a child that is still running.
    [Test]
    public async Task Handoff_past_the_cap_reports_an_unquiesced_lane_and_closes_auto_actions() {
        var never = new TaskCompletionSource();
        var channel = new OutcomeChannel();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var quiesced = await AppUnderTest
            .HandoffAfterWizardAsync(auth: null, install: null, () => never.Task, TimeSpan.FromMilliseconds(50), channel, TimeProvider.System)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(quiesced).IsFalse();
        // Even a Complete gate builds the degraded graph while the lane still owns a live action.
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete(), quiesced)).IsTrue();
        _ = channel.ConsumeAsync(CancellationToken.None); // past the cap the channel is still handed over
    }

    [Test]
    public async Task Handoff_within_the_cap_reports_a_quiesced_lane_and_leaves_auto_actions_open() {
        var quiesced = await AppUnderTest
            .HandoffAfterWizardAsync(auth: null, install: null, () => Task.CompletedTask, Cap, new OutcomeChannel(), TimeProvider.System)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(quiesced).IsTrue();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete(), quiesced)).IsFalse();
        // An incomplete gate still closes them, quiesced or not.
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Incomplete(GateReason.NoToken), quiesced))
            .IsTrue();
    }

    [Test]
    public async Task An_unquiesced_lane_is_announced_exactly_once_and_a_quiesced_one_says_nothing() {
        var announced = new FakeLifecycleSurface();
        var silent = new FakeLifecycleSurface();

        AppUnderTest.AnnounceUnquiescedLane(announced, laneQuiesced: false);
        AppUnderTest.AnnounceUnquiescedLane(silent, laneQuiesced: true);

        await Assert.That(announced.AttentionMessages).IsEquivalentTo([AppUnderTest.LaneStillBusyAttention]);
        await Assert.That(silent.AttentionMessages).IsEmpty();
        await Assert.That(announced.StatusMessages).IsEmpty();
    }

    /// The mirror of the post-boundary ordering test, for the WITHIN-cap path: the channel stays
    /// wizard-owned until the lane's own quiesce completes — the transfer is the last step.
    [Test]
    public async Task Handoff_transfers_only_after_the_lane_quiesces_within_the_cap() {
        var quiesce = new TaskCompletionSource();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
            WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        var handoff = AppUnderTest.HandoffAfterWizardAsync(
            auth: null, install: null, () => quiesce.Task, TimeSpan.FromSeconds(30), channel, TimeProvider.System);
        await Task.Delay(50);

        await Assert.That(handoff.IsCompleted).IsFalse();
        Assert.Throws<InvalidOperationException>(() => { _ = channel.ConsumeAsync(CancellationToken.None); });

        quiesce.SetResult();

        await Assert.That(await handoff.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        _ = channel.ConsumeAsync(CancellationToken.None); // transferred only now

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── the consumer runs unchanged over the wizard's own surface ─────────────

    /// A withdrawn line clears only while it is still the one shown; a newer line stays.
    [Test]
    public async Task The_wizard_surface_withdraws_only_the_line_it_still_shows() {
        var surface = new WizardLifecycleSurface((_, _) => Task.FromResult(false), action => action());
        const string report = "Daemon daemon-a is running for another server.";

        surface.Attention(report);
        surface.Withdraw(report);
        await Assert.That(surface.AttentionText).IsNull();

        surface.Attention(report);
        surface.Attention("newer");
        surface.Withdraw(report);
        await Assert.That(surface.AttentionText).IsEqualTo("newer");
    }

    [Test]
    public async Task The_outcome_consumer_presents_through_the_wizard_surface() {
        var surface = new WizardLifecycleSurface((_, _) => Task.FromResult(false), action => action());
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token, AppUnderTest.WizardAttentionCopyFor);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => surface.AttentionText is not null, what: "the wizard-surface presentation");

        await Assert.That(surface.AttentionText).IsEqualTo(AppUnderTest.WizardAttentionCopyFor("internal_error")!);
        await Assert.That(surface.AttentionText!).DoesNotContain("Start daemon");

        await cts.CancelAsync();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// Blocks its FIRST Attention call on a gate, so the envelope is provably in flight on the
    /// wizard consumer at the moment the handoff runs.
    sealed class GatedAttentionSurface(Task gate) : ILifecycleSurface {
        public int Entered;
        public int Presented;

        public void Status(string message) { }

        public void Attention(string message) {
            Interlocked.Increment(ref Entered);
            gate.GetAwaiter().GetResult();
            Interlocked.Increment(ref Presented);
        }

        public Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => Task.FromResult(false);
        public Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => Task.FromResult<bool?>(false);
    }

    [Test]
    public async Task An_envelope_in_flight_across_the_handoff_is_presented_exactly_once() {
        var gate = new TaskCompletionSource();
        var wizardSurface = new GatedAttentionSurface(gate.Task);
        var rootSurface = new FakeLifecycleSurface();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, wizardSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => wizardSurface.Entered == 1, what: "the wizard consumer's presentation");

        await AppUnderTest.HandoffAfterWizardAsync(auth: null, install: null, () => Task.CompletedTask, Cap, channel, TimeProvider.System);
        var rootConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, rootSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        gate.SetResult(); // the wizard consumer finishes its presentation and acks
        await WizardFixtures.WaitUntilAsync(() => wizardSurface.Presented == 1, what: "the acked presentation");
        await Task.Delay(150); // give a wrong re-delivery to the root consumer every chance to fire

        await Assert.That(wizardSurface.Presented + rootSurface.AttentionMessages.Count).IsEqualTo(1);

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
        await rootConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task An_envelope_enqueued_after_the_handoff_reaches_the_root_consumer_only() {
        var wizardSurface = new FakeLifecycleSurface();
        var rootSurface = new FakeLifecycleSurface();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, wizardSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        await AppUnderTest.HandoffAfterWizardAsync(auth: null, install: null, () => Task.CompletedTask, Cap, channel, TimeProvider.System);
        var rootConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, rootSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => rootSurface.AttentionMessages.Count == 1, what: "the root presentation");

        await Assert.That(rootSurface.AttentionMessages[0]).IsEqualTo(AppUnderTest.AttentionCopyFor("internal_error")!);
        await Assert.That(wizardSurface.AttentionMessages).IsEmpty();

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
        await rootConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── shutdown during wizard mode ───────────────────────────────────────────

    [Test]
    public async Task Shutdown_quiesce_cancels_the_wizard_sign_in_and_waits_for_the_lane() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new AuthResult.Cancelled();
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Discover(ForceDevice: true));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.QuiesceAppAsync(graph.Auth, import: null, install: null, lifecycle: null, lane: null, Cap, TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(attempt.Result.IsCompleted).IsTrue();
            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();

            return true;
        });
    }

    [Test]
    public async Task Shutdown_quiesce_with_no_wizard_is_the_existing_lifecycle_and_lane_wait() {
        await AppUnderTest.QuiesceAppAsync(auth: null, import: null, install: null, lifecycle: null, lane: null, Cap, TimeProvider.System)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// The cap is the LANE's, never the sign-in's — a post-boundary operation that is
    /// still publishing when the cap window has long expired is awaited to its terminal answer.
    [Test]
    public async Task Shutdown_quiesce_awaits_a_post_boundary_sign_in_long_past_the_cap() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            harness.Operation = async (_, _) => {
                entered.TrySetResult();
                await release.Task;
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, "someone", []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var quiesce = AppUnderTest.QuiesceAppAsync(
                graph.Auth, import: null, install: null, lifecycle: null, lane: null, cap: TimeSpan.FromMilliseconds(20),
                time: TimeProvider.System);
            await Task.Delay(400); // twenty cap windows

            await Assert.That(quiesce.IsCompleted).IsFalse();

            release.SetResult();
            await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Committed>();

            return true;
        });
    }

    /// A quit mid-upgrade interrupts it once and waits, uncapped, for the CLI to say how it ended:
    /// past the download nothing stops for the interrupt, and the app must not exit under a switch.
    [Test]
    public async Task Shutdown_quiesce_interrupts_a_local_upgrade_once_and_awaits_its_end_past_the_cap() {
        var interrupts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli {
            LocalUpgradeBehavior = async (_, interrupt, _) => {
                await using (interrupt.Register(() => Interlocked.Increment(ref interrupts))) await release.Task;
                return new StreamingResult(0, false, []);
            },
        };
        var upgrade = new LocalUpgradeController(() => cli, action => action());
        await upgrade.StartAsync(CancellationToken.None);

        var quiesce = AppUnderTest.QuiesceAppAsync(
            auth: null, import: null, install: null, lifecycle: null, lane: null, cap: TimeSpan.FromMilliseconds(20),
            time: TimeProvider.System, upgrade: upgrade);
        await Task.Delay(400);

        await Assert.That(quiesce.IsCompleted).IsFalse();
        await Assert.That(interrupts).IsEqualTo(1);

        release.SetResult();
        await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(upgrade.Outcome).IsTypeOf<LocalUpgradeOutcome.Upgraded>();
    }

    // The other half: the lifecycle/lane wait is still the capped one.
    [Test]
    public async Task Shutdown_quiesce_still_caps_an_in_flight_lane_mutation() {
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        await using var lane = new DaemonMutationLane(Daemons.Store,
            new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            new OutcomeChannel(),
            () => "/opt/kcap/bin/kcap",
            (_, _) => cli,
            _ => new WizardFixtures.NeverObservation(),
            TimeProvider.System);

        var runTask = lane.RunAsync(
            new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a"),
            CancellationToken.None);

        await AppUnderTest
            .QuiesceAppAsync(auth: null, import: null, install: null, lifecycle: null, lane, TimeSpan.FromMilliseconds(50), TimeProvider.System)
            .WaitAsync(TimeSpan.FromSeconds(5));

        gate.SetResult("9.9.9");
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // The close contract (KillTree + await exit) must run from the close boundary, not only CanLeaveAsync.
    static ImportDiscoveryReport OneRepo() =>
        new([new ImportDiscoveryRepo("acme", "web", 3, DateTimeOffset.UnixEpoch, [])], 0, []);

    static async Task<HistoryStepViewModel> ImportingAsync(FakeKcapCli cli) {
        cli.DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(OneRepo());
        var history = new HistoryStepViewModel(cli, () => [HarnessId.Claude], action => action(), "test-mac", TimeProvider.System);
        await history.OnEnterAsync(CancellationToken.None);
        await history.Discovery;
        await history.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        return history;
    }

    /// The import outlives onboarding: closing the wizard settles sign-in and hands the channel on,
    /// and leaves the run to finish in the main window.
    [Test]
    public async Task Handoff_leaves_a_running_history_import_alone() {
        await AvaloniaSession.DispatchAsync(async () => {
            var cli = new FakeKcapCli();
            var entered = new TaskCompletionSource();
            var cancelled = false;
            cli.ImportBehavior = async (_, _, ct) => {
                entered.TrySetResult();
                await using var reg = ct.Register(() => cancelled = true);
                await Task.Delay(Timeout.Infinite, ct);
                return new StreamingResult(0, false, []);
            };

            var history = await ImportingAsync(cli);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.HandoffAfterWizardAsync(auth: null, install: null, () => Task.CompletedTask, Cap, new OutcomeChannel(), TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(cancelled).IsFalse();
            await Assert.That(history.Run!.Running).IsTrue();

            await history.CancelActiveRunAsync();

            return true;
        });
    }

    [Test]
    public async Task Shutdown_quiesce_cancels_an_in_flight_import_and_awaits_it() {
        await AvaloniaSession.DispatchAsync(async () => {
            var cli = new FakeKcapCli();
            var entered = new TaskCompletionSource();
            cli.ImportBehavior = async (_, _, ct) => {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new StreamingResult(0, false, []);
            };

            var history = await ImportingAsync(cli);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.QuiesceAppAsync(auth: null, history, install: null, lifecycle: null, lane: null, Cap, TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(history.Run!.State).IsEqualTo(ImportRunState.Cancelled);

            return true;
        });
    }

    // ── closing the wizard during a local install ─────────────────────────────

    /// A `kcap setup --local` child that stops only when interrupted, and exits only once released.
    sealed class InterruptibleSetup {
        public int Signals;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StreamingResult> RunAsync(Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct) {
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (interrupt.Register(() => {
                Interlocked.Increment(ref Signals);
                interrupted.TrySetResult();
            })) {
                Entered.TrySetResult();
                await interrupted.Task;
            }
            await Release.Task;
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("cancelled")));

            return new StreamingResult(130, false, []);
        }
    }

    // RunWizardModeAsync over the harness: one install controller across every wizard shown, then
    // show, let the test drive, wait for the close, hand off.
    static Func<GateResult, ProfileContext?, Task<bool>> WizardRuns(
            WizardFixtures.GraphHarness harness, List<(WizardGraph Graph, WizardStepId StartedAt)> shown,
            Func<WizardGraph, Task> drive, CancellationToken shutdown, StrongBox<LocalInstallController?>? install = null) {
        install ??= new StrongBox<LocalInstallController?>();

        return async (_, _) => {
            var options = harness.Options();
            install.Value ??= WizardComposition.NewInstallController(options);
            var graph = WizardComposition.BuildGraph(options with { Install = install.Value });
            await graph.ViewModel.PendingEnterForTesting;
            shown.Add((graph, graph.ViewModel.Current.Id));
            await drive(graph);
            await AppUnderTest.WaitForWizardCloseAsync(graph.ViewModel, shutdown);

            return await AppUnderTest.HandoffAfterWizardAsync(
                graph.Auth, graph.Install, () => Task.CompletedTask, Cap, new OutcomeChannel(), TimeProvider.System);
        };
    }

    Func<Task<(GateResult Gate, ProfileContext? Profiles)>> Reevaluate(WizardFixtures.GraphHarness harness) =>
        () => AppUnderTest.ResolveAndEvaluateGateAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), ProfileOverrides.None, harness.LocalRoot, TimeProvider.System,
            CancellationToken.None);

    [Test]
    public async Task A_close_with_pending_local_setup_reopens_the_wizard_and_builds_no_graph() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        // An install that already failed leaves the journal standing, which is what the close re-reads.
        const string message = "The download stalled.";
        harness.Cli.SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed", message));
        using var shutdown = new CancellationTokenSource();
        var shown = new List<(WizardGraph Graph, WizardStepId StartedAt)>();
        var builds = 0;
        var buildsWhenReopened = -1;
        string? reopenedMessage = null;

        await AvaloniaSession.DispatchAsync(async () => {
            var runs = WizardRuns(harness, shown, graph => {
                if (shown.Count == 1) {
                    graph.ViewModel.RequestClose();
                } else {
                    buildsWhenReopened = builds;
                    reopenedMessage = graph.Steps.OfType<InstallStepViewModel>().Single().FailureDetail;
                    shutdown.Cancel(); // ends the test's loop; a quit builds nothing either
                }

                return Task.CompletedTask;
            }, shutdown.Token);

            await AppUnderTest.RunWizardThenBuildGraphAsync(
                    new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true, runs,
                    Reevaluate(harness), (_, _, _) => builds++, () => LocalSetupState.Pending(harness.LocalRoot), shutdown.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        });

        await Assert.That(shown.Select(s => s.StartedAt).ToList()).IsEquivalentTo([WizardStepId.Install, WizardStepId.Install]);
        await Assert.That(buildsWhenReopened).IsEqualTo(0);
        await Assert.That(builds).IsEqualTo(0);
        // The reopened wizard shows the settled failure; it does not start a second child unasked.
        await Assert.That(harness.Cli.SetupLocalCallCount).IsEqualTo(1);
        await Assert.That(reopenedMessage).IsEqualTo(message);
    }

    /// A journal the CLI cannot read settles Permanent: no Try again and no Back, so reopening the
    /// wizard on every close would leave only Quit.
    [Test]
    public async Task A_close_after_a_permanent_setup_failure_builds_one_degraded_graph() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var root = new LocalRoot(Config.PathTo("local"));
        root.EnsureCreated();
        Config.CreateFile("local/journal.json", "{not json");
        LocalEmbeddingFile.Write(root, LocalEmbeddingSetting.Ollama("mxbai-embed-large"));
        harness.LocalRoot = root;
        harness.Cli.SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("registry_unreadable", "The journal cannot be read."));
        using var shutdown = new CancellationTokenSource();
        var shown = new List<(WizardGraph Graph, WizardStepId StartedAt)>();
        var install = new StrongBox<LocalInstallController?>();
        var built = new List<(GateResult Gate, bool LaneQuiesced)>();

        await AvaloniaSession.DispatchAsync(async () => {
            var runs = WizardRuns(harness, shown, async graph => {
                if (shown.Count > 1) {
                    shutdown.Cancel(); // a reopen is the failure; end the loop it would otherwise repeat
                    return;
                }
                await WizardFixtures.WaitUntilAsync(() => graph.Install.Outcome is not null, what: "the install to settle");
                graph.ViewModel.RequestClose();
            }, shutdown.Token, install);

            await AppUnderTest.RunWizardThenBuildGraphAsync(
                    new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true, runs,
                    Reevaluate(harness), (gate, _, quiesced) => built.Add((gate, quiesced)),
                    () => LocalSetupState.Pending(harness.LocalRoot), shutdown.Token,
                    () => AppUnderTest.LocalSetupCannotRetry(install.Value))
                .WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        });

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(built.Count).IsEqualTo(1);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(built[0].Gate, built[0].LaneQuiesced)).IsTrue();
    }

    /// A profile mismatch refuses before any child starts, and a pending journal locks Edition, so the
    /// page has no way forward either: a close must not reopen it.
    [Test]
    public async Task A_close_after_a_profile_refusal_under_a_pending_setup_builds_one_degraded_graph() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = "personal", Profiles = new() { ["personal"] = new Profile() } });
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot       = LocalSetupScript.PendingSetup(Config);
        harness.Identity        = null;
        harness.ProfileOverride = "work";
        using var shutdown = new CancellationTokenSource();
        var shown = new List<(WizardGraph Graph, WizardStepId StartedAt)>();
        var install = new StrongBox<LocalInstallController?>();
        var built = new List<(GateResult Gate, bool LaneQuiesced)>();

        await AvaloniaSession.DispatchAsync(async () => {
            var runs = WizardRuns(harness, shown, graph => {
                if (shown.Count > 1) shutdown.Cancel(); // a reopen is the failure; end the loop it would otherwise repeat
                else graph.ViewModel.RequestClose();

                return Task.CompletedTask;
            }, shutdown.Token, install);

            await AppUnderTest.RunWizardThenBuildGraphAsync(
                    new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true, runs,
                    Reevaluate(harness), (gate, _, quiesced) => built.Add((gate, quiesced)),
                    () => LocalSetupState.Pending(harness.LocalRoot), shutdown.Token,
                    () => AppUnderTest.LocalSetupCannotRetry(install.Value))
                .WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        });

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(built.Count).IsEqualTo(1);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(built[0].Gate, built[0].LaneQuiesced)).IsTrue();
        await Assert.That(harness.Cli.SetupLocalCallCount).IsEqualTo(0);
    }

    /// A gate that could not be read says nothing about the journal: pending setup still reopens the wizard.
    [Test]
    public async Task A_close_with_pending_setup_reopens_the_wizard_even_when_the_gate_cannot_be_read() {
        var pending = true;
        var shows = 0;
        var builds = 0;

        await AppUnderTest.RunWizardThenBuildGraphAsync(
                new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true,
                (_, _) => {
                    if (++shows == 2) pending = false;
                    return Task.FromResult(true);
                },
                () => Task.FromResult<(GateResult, ProfileContext?)>((new GateResult.Incomplete(GateReason.EvaluationFailed), null)),
                (_, _, _) => builds++, () => pending, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(shows).IsEqualTo(2);
        await Assert.That(builds).IsEqualTo(1);
    }

    [Test]
    public async Task A_close_after_installed_builds_exactly_one_graph() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var root = LocalSetupScript.PendingSetup(Config);
        harness.LocalRoot = root;
        harness.Cli.SetupLocalBehavior = (onLine, _, _) => {
            LocalStateFiles.ClearJournal(root);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            return Task.FromResult(new StreamingResult(0, false, []));
        };
        var shown = new List<(WizardGraph Graph, WizardStepId StartedAt)>();
        var built = new List<GateResult>();

        await AvaloniaSession.DispatchAsync(async () => {
            var runs = WizardRuns(harness, shown, graph => {
                graph.ViewModel.RequestClose();
                return Task.CompletedTask;
            }, CancellationToken.None);

            await AppUnderTest.RunWizardThenBuildGraphAsync(
                    new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true, runs,
                    Reevaluate(harness), (gate, _, _) => built.Add(gate), () => LocalSetupState.Pending(harness.LocalRoot), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        });

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(built.Count).IsEqualTo(1);
        await Assert.That(built[0]).IsNotEqualTo(new GateResult.Incomplete(GateReason.LocalSetupPending));
    }

    [Test]
    public async Task Quit_during_install_settles_the_child_and_builds_no_graph() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var setup = new InterruptibleSetup();
        harness.Cli.SetupLocalBehavior = setup.RunAsync;
        using var shutdown = new CancellationTokenSource();
        var shown = new List<(WizardGraph Graph, WizardStepId StartedAt)>();
        var builds = 0;
        Task? quit = null;

        await AvaloniaSession.DispatchAsync(async () => {
            var runs = WizardRuns(harness, shown, async graph => {
                await setup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // The shutdown path and the startup handoff both settle the same running child.
                quit = AppUnderTest.QuiesceAppAsync(
                    auth: null, import: null, graph.Install, lifecycle: null, lane: null, Cap, TimeProvider.System);
                await shutdown.CancelAsync();
                _ = Task.Delay(100).ContinueWith(_ => setup.Release.TrySetResult(), TaskScheduler.Default);
            }, shutdown.Token);

            await AppUnderTest.RunWizardThenBuildGraphAsync(
                    new GateResult.Incomplete(GateReason.LocalSetupPending), null, showWizard: true, runs,
                    Reevaluate(harness), (_, _, _) => builds++, () => LocalSetupState.Pending(harness.LocalRoot), shutdown.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await quit!.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        });

        var install = shown.Single().Graph.Install;
        await Assert.That(setup.Signals).IsEqualTo(1);
        await Assert.That(install.Running).IsFalse();
        await Assert.That(install.Outcome).IsTypeOf<LocalInstallOutcome.Failed>();
        await Assert.That(builds).IsEqualTo(0);
    }

    [Test]
    public async Task Cancel_then_quit_sends_one_interrupt() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var setup = new InterruptibleSetup();
        harness.Cli.SetupLocalBehavior = setup.RunAsync;

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            await setup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await graph.Steps.OfType<InstallStepViewModel>().Single().CancelCommand.Execute().ToTask();
            var quit = AppUnderTest.QuiesceAppAsync(
                auth: null, import: null, graph.Install, lifecycle: null, lane: null, Cap, TimeProvider.System);
            await Task.Delay(50);

            await Assert.That(quit.IsCompleted).IsFalse(); // the quit waits for the child's exit
            setup.Release.SetResult();
            await quit.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(setup.Signals).IsEqualTo(1);
            await Assert.That(graph.Install.Running).IsFalse();

            return true;
        });
    }

    /// A titlebar close mid-install undoes the install with the window still open on Install, and
    /// only a close after the settle closes the wizard.
    [Test]
    public async Task A_window_close_during_install_interrupts_and_keeps_the_wizard_on_install() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var setup = new InterruptibleSetup();
        harness.Cli.SetupLocalBehavior = setup.RunAsync;

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            await setup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var heldFirst  = AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: true);
            var heldSecond = AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: true);

            await Assert.That(heldFirst).IsTrue();
            await Assert.That(heldSecond).IsTrue();
            await Assert.That(graph.ViewModel.Closed).IsFalse();
            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.Install);

            setup.Release.SetResult();
            await graph.Install.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(setup.Signals).IsEqualTo(1);
            await Assert.That(graph.ViewModel.Closed).IsFalse();
            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.Install);

            await Assert.That(AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: true)).IsFalse();
            await Assert.That(graph.ViewModel.Closed).IsTrue();

            return true;
        });
    }

    /// A quit closes the window through the shutdown path, which settles the install itself.
    [Test]
    public async Task A_shutdown_close_during_install_is_not_held() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.LocalRoot = LocalSetupScript.PendingSetup(Config);
        var setup = new InterruptibleSetup();
        harness.Cli.SetupLocalBehavior = setup.RunAsync;

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            await setup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: false)).IsFalse();
            await Assert.That(graph.ViewModel.Closed).IsTrue();
            await Assert.That(setup.Signals).IsEqualTo(0);

            setup.Release.SetResult();
            await graph.Install.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        });
    }

    /// Before a server is connected a closed window quits rather than opening an empty app; once one
    /// is, it hands over. A finish that leaves the window up never quits.
    [Test]
    [Arguments(false, true, true)]
    [Arguments(false, false, false)]
    [Arguments(true, true, false)]
    public async Task A_closed_window_quits_only_before_a_server_is_connected(bool connected, bool windowClosed, bool quits) {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var quit = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { AlreadyAuthenticated = connected });
            await graph.ViewModel.PendingEnterForTesting;
            AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: true);

            return AppUnderTest.QuitsAfterWizardClose(graph.ViewModel, windowClosed);
        });

        await Assert.That(quit).IsEqualTo(quits);
    }

    /// A close on Install once the server is up is connected, so the main window opens.
    [Test]
    public async Task A_closed_window_after_a_local_install_hands_over() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var root = LocalSetupScript.PendingSetup(Config);
        harness.LocalRoot = root;
        harness.Cli.SetupLocalBehavior = (onLine, _, _) => {
            LocalStateFiles.ClearJournal(root);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Committed));
            return Task.FromResult(new StreamingResult(0, false, []));
        };

        var quit = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            await WizardFixtures.WaitUntilAsync(() => graph.Install.Outcome is not null, what: "the install to settle");
            AppUnderTest.WizardWindowClosing(graph.ViewModel, graph.Install, userClose: true);

            return AppUnderTest.QuitsAfterWizardClose(graph.ViewModel, windowClosed: true);
        });

        await Assert.That(quit).IsFalse();
    }

    // ── the wizard graph: composition invariants ──────────────────────────────

    [Test]
    public async Task The_wizard_graph_builds_every_step_and_touches_no_daemon() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());

            await Assert.That(graph.Steps.Select(s => s.Id).ToList()).IsEquivalentTo([
                WizardStepId.Edition, WizardStepId.Install, WizardStepId.SignIn,
                WizardStepId.Harnesses, WizardStepId.Import, WizardStepId.Privacy, WizardStepId.Done,
            ], CollectionOrdering.Matching);
            // No mutation, no IPC, no status read: composing the wizard never speaks to a daemon.
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Observation.Calls).IsEqualTo(0);
            // Never composed once: the socket is only bound when a step actually puts to it (the
            // CLI factory IS consulted here, for the pure CliPath the vendor steps gate on).
            await Assert.That(harness.OpsFactoryCalls).IsEqualTo(0);

            return true;
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task The_path_fix_is_offered_on_the_harnesses_page_only_when_applicable(bool applicable) {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.ShimApplicable = applicable;

            var graph = WizardComposition.BuildGraph(harness.Options());
            var harnesses = graph.Steps.OfType<HarnessesStepViewModel>().Single();

            await Assert.That(harnesses.PathFix is not null).IsEqualTo(applicable);
            await Assert.That(harnesses.PathHazard).IsEqualTo(applicable);

            return true;
        });
    }

    /// History reads exactly the harnesses the Harnesses page left recording.
    [Test]
    public async Task History_discovers_for_the_harnesses_left_recording() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.ShimApplicable = false; // this page leaves only once the login shell can find kcap
            harness.Detected = VendorDetection.Build("claude", "cursor");

            var graph = WizardComposition.BuildGraph(harness.Options());
            var harnesses = graph.Steps.OfType<HarnessesStepViewModel>().Single();

            await harnesses.OnEnterAsync(CancellationToken.None);
            harnesses.Visibility = "private";
            harnesses.Rows.Single(r => r.Label == "Cursor").Record = false;
            await harnesses.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            await graph.History.OnEnterAsync(CancellationToken.None);
            await graph.History.Discovery;

            await Assert.That(harness.Cli.DiscoverCalls.Single()).IsEquivalentTo(["--claude"]);
            await Assert.That(harness.DetectionFactoryCalls).IsEqualTo(1);

            return true;
        });
    }

    [Test]
    public async Task The_daemon_enabler_reads_status_through_a_freshly_resolved_cli() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));
            harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the row's own precondition

            await graph.Daemon.EnsureAsync(CancellationToken.None);
            var afterFirst = harness.CliFactoryCalls;
            await graph.Daemon.EnsureAsync(CancellationToken.None);

            // Every status read rebinds — a name saved since the last one lands on the next.
            await Assert.That(afterFirst).IsGreaterThan(0);
            await Assert.That(harness.CliFactoryCalls).IsGreaterThan(afterFirst);

            return true;
        });
    }

    [Test]
    public async Task The_daemon_enabler_requires_a_committed_sign_in_and_installs_once_it_lands() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));
            harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false));

            var graph = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Daemon;
            var connect = graph.Connect;
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();

            await daemon.EnsureAsync(CancellationToken.None);
            var beforeSignIn = (daemon.Row, harness.Cli.StatusCallCount, harness.Lane.Requests.Count);

            connect.Choice = ConnectChoice.Paste;
            connect.ServerInputText = "https://acme.example";
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await daemon.EnsureAsync(CancellationToken.None); // waits out the ensure sign-in kicked

            await Assert.That(beforeSignIn).IsEqualTo((DaemonRow.RequiresSignIn, 0, 0));
            await Assert.That(signIn.Satisfied).IsTrue();
            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.NotInstalled);
            await Assert.That(harness.Lane.Requests.Single().Verb).IsEqualTo(MutationVerb.Install);
            await Assert.That(daemon.Satisfied).IsTrue();

            return true;
        });
    }

    // ── the composition seam: one façade, arming hook wired ───────────────────

    [Test]
    public async Task The_facade_is_built_from_the_bridges_own_sink_and_picker() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            WizardComposition.BuildGraph(harness.Options());

            await Assert.That(harness.Spec).IsNotNull();
            await Assert.That(harness.Spec!.Progress).IsSameReferenceAs(harness.Bridges.Progress);
            await Assert.That(harness.Spec.Picker).IsSameReferenceAs(harness.Bridges.Picker);

            return true;
        });
    }

    [Test]
    public async Task The_facades_beforeCommit_hook_arms_a_durable_claim_per_identity() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            WizardComposition.BuildGraph(harness.Options());
            await harness.Spec!.BeforeCommit(
                [new AuthIdentity("acme", "https://acme.example:443"), new AuthIdentity("work", "https://work.example:443")],
                CancellationToken.None);

            await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo(["acme", "work"]);

            return true;
        });
    }

    [Test]
    public async Task The_production_bridges_marshal_through_the_avalonia_dispatcher() {
        var marshalled = await AvaloniaSession.DispatchAsync(async () => {
            var bridges = WizardComposition.BuildBridges(
                action => Dispatcher.UIThread.Post(action), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);
            var posted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Posted from a background thread, exactly as the façade's flows raise their events.
            await Task.Run(() => bridges.Post(() => posted.TrySetResult(Dispatcher.UIThread.CheckAccess())));
            Dispatcher.UIThread.RunJobs();

            return await posted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(marshalled).IsTrue();
    }

    // ── the Done step's facts ──────────────────────────────────────────────────

    [Test]
    public async Task Done_does_not_claim_existing_connections_were_removed() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();
            await done.OnEnterAsync(CancellationToken.None);

            await Assert.That(done.Capture).IsEmpty();
            await Assert.That(done.OpenInBrowserVisible).IsFalse();
            await Assert.That(done.ShowsFigures).IsFalse();

            return true;
        });
    }

    // ── the shim step's pre-probe (latency the shipping default must not pay) ─

    [Test]
    [Arguments(false, "/opt/kcap/bin/kcap")] // no installer for this OS: the answer cannot change the outcome
    [Arguments(true, null)]                  // no linkable target: same
    public async Task The_path_probe_is_skipped_when_it_cannot_change_the_shim_decision(bool hasInstaller, string? target) {
        var probes = 0;

        var applicable = await AppUnderTest.ResolveShimApplicableAsync(hasInstaller, target, _ => {
            probes++;
            return Task.FromResult<bool?>(false);
        }, CancellationToken.None);

        await Assert.That(applicable).IsFalse();
        await Assert.That(probes).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, true)]  // kcap is NOT on the terminal PATH — the step has something to offer
    [Arguments(true, false)]  // already on PATH
    [Arguments(null, false)]  // unknown probe: never offer on an inconclusive read
    public async Task On_macos_with_a_target_the_probe_decides(bool? onPath, bool expected) {
        var probes = 0;

        var applicable = await AppUnderTest.ResolveShimApplicableAsync(true, "/opt/kcap/bin/kcap", _ => {
            probes++;
            return Task.FromResult(onPath);
        }, CancellationToken.None);

        await Assert.That(applicable).IsEqualTo(expected);
        await Assert.That(probes).IsEqualTo(1);
    }

    [Test]
    public async Task A_probe_failure_is_unknown_but_a_quit_during_it_propagates() {
        var applicable = await AppUnderTest.ResolveShimApplicableAsync(
            true, "/opt/kcap/bin/kcap", _ => throw new InvalidOperationException("probe boom"), CancellationToken.None);

        await Assert.That(applicable).IsFalse(); // degraded to unknown, never "offer it anyway"

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // A quit mid-probe must not go on to build a wizard window — same rule as the gate's.
        await Assert.ThrowsAsync<OperationCanceledException>(() => AppUnderTest.ResolveShimApplicableAsync(
            true, "/opt/kcap/bin/kcap", ct => Task.FromCanceled<bool?>(ct), cts.Token));
    }

    // ── the sign-in step's committed answer ───────────────────────────────────

    static AuthResult.Committed CommittedSignIn() =>
        new(ProfileConfig.DefaultName, "https://acme.example:443", AuthProvider.WorkOS, "sam", []);

    static async Task<(WizardGraph Graph, SignInStepViewModel SignIn)> OnTheSignInStepAsync(WizardFixtures.GraphHarness harness) {
        var graph = WizardComposition.BuildGraph(harness.Options());
        await graph.ViewModel.PendingEnterForTesting;

        await graph.Steps.OfType<EditionStepViewModel>().Single().SignInCommand.Execute().ToTask();
        await WizardFixtures.WaitUntilAsync(
            () => graph.ViewModel.Current.Id == WizardStepId.SignIn, what: "the jump to the sign-in step");

        return (graph, graph.Steps.OfType<SignInStepViewModel>().Single());
    }

    /// A committed sign-in stays put. The forward button is what leaves the page.
    [Test]
    public async Task A_committed_sign_in_stays_until_the_user_continues() {
        await AvaloniaSession.DispatchAsync(async () => {
            var time = new FakeTimeProvider();
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { Time = time };
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(CommittedSignIn());

            var (graph, signIn) = await OnTheSignInStepAsync(harness);
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));

            time.Advance(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.SignIn);

            await graph.ViewModel.NextCommand.Execute().ToTask();

            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.Harnesses);

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ── late binding (the adapters the daemon enabler is composed over) ──────────

    [Test]
    public async Task Late_bound_ops_resolve_a_fresh_socket_per_call() {
        var first = new ScriptedLocalControlOps();
        var second = new ScriptedLocalControlOps();
        var current = first;
        var ops = new LateBoundLocalControlOps(() => current);

        first.QueueGet(new ConsentPolicyDto("allow", 300, []));
        await ops.GetConsentPolicyAsync(CancellationToken.None);

        current = second; // the Defaults step renamed the daemon between calls
        second.QueuePutV2(true, null);
        await ops.PutConsentPolicyV2Async(
            new ConsentPolicyPutV2Dto("renamed", "https://acme.example:443", new ConsentPolicyDto("prompt", 300, [])),
            CancellationToken.None);

        await Assert.That(first.GetCalls).IsEqualTo(1);
        await Assert.That(first.PutV2Calls).IsEqualTo(0);
        await Assert.That(second.PutV2Calls).IsEqualTo(1);
        await Assert.That(second.PutV2Payloads[0].ExpectedName).IsEqualTo("renamed");
    }

    [Test]
    public async Task Late_bound_cli_resolves_a_fresh_binding_per_call() {
        var first = new FakeKcapCli { CliPath = "/one/kcap" };
        var second = new FakeKcapCli { CliPath = "/two/kcap" };
        var current = first;
        var binds = 0;
        var cli = new LateBoundKcapCli(() => { binds++; return current; }, "/opt/kcap/bin/kcap");

        await cli.ServiceStatusAsync(CancellationToken.None);
        current = second;
        await cli.ServiceStatusAsync(CancellationToken.None);

        await Assert.That(first.StatusCallCount).IsEqualTo(1);
        await Assert.That(second.StatusCallCount).IsEqualTo(1);
        // CliPath is the captured binary — a UI binding must not pay a config load per get.
        await Assert.That(cli.CliPath).IsEqualTo("/opt/kcap/bin/kcap");
        _ = cli.CliPath;
        await Assert.That(binds).IsEqualTo(2); // the two status calls only
    }

    // ── window/dialog routing ─────────────────────────────────────────────────

    sealed class StubStep(WizardStepId id, string title) : IWizardStep {
        public WizardStepId Id => id;
        public string Title => title;
        public bool Applicable => true;
        public bool Satisfied => false;
        public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);
    }

    static (OnboardingViewModel Wizard, WizardLifecycleSurface Surface) NewShell() {
        var surface = new WizardLifecycleSurface((_, _) => Task.FromResult(false), action => action());
        var wizard = new OnboardingViewModel(
            [new StubStep(WizardStepId.Edition, "Connect to kcap")], CancellationToken.None, surface);

        return (wizard, surface);
    }

    /// The daemon's outcome lines land in the Done page's service card, behind See details, and the
    /// shell's footer carries none of them.
    [Test]
    public async Task A_daemon_outcome_line_lands_in_the_done_pages_service_card() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var (details, footerTexts) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();
            await done.OnEnterAsync(CancellationToken.None);

            harness.Surface.Attention(AppUnderTest.WizardAttentionCopyFor("verify_viability")!);

            var window = new MainWindow { Onboarding = graph.ViewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var footer = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "StepFooter")
                .GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (done.ServiceDetails, footer);
        });

        await Assert.That(details).Contains(AppUnderTest.WizardAttentionCopyFor("verify_viability")!);
        await Assert.That(footerTexts).DoesNotContain(AppUnderTest.WizardAttentionCopyFor("verify_viability")!);
    }

    [Test]
    public async Task A_lifecycle_prompt_opens_a_dialog_owned_by_the_wizard_window() {
        var (owned, settled) = await AvaloniaSession.DispatchAsync(() => {
            var (wizard, _) = NewShell();
            var window = new MainWindow { Onboarding = wizard };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var cts = new CancellationTokenSource();
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindTakeover, null, null, false, "disclosure text");
            var answer = AppUnderTest.ShowLifecyclePromptDialogAsync(window, prompt, cts.Token);
            Dispatcher.UIThread.RunJobs();

            var dialogOpen = window.OwnedWindows.Count > 0;

            cts.Cancel(); // the wizard closing must never strand the dialog
            Dispatcher.UIThread.RunJobs();

            var resolvedFalse = answer.IsCompletedSuccessfully && !answer.Result;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (dialogOpen, resolvedFalse);
        }).WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.That(owned).IsTrue();
        await Assert.That(settled).IsTrue();
    }

    [Test]
    public async Task The_wizard_is_the_main_window_and_closing_it_never_exits_the_app() {
        var (isWizard, visible, mode) = await AvaloniaSession.DispatchAsync(() => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // pinned by OnFrameworkInitializationCompleted
            var (wizard, _) = NewShell();

            var window = AppUnderTest.ShowWizardWindow(desktop, wizard);
            Dispatcher.UIThread.RunJobs();

            var result = (ReferenceEquals(fake.MainWindow, window), window.IsVisible, fake.ShutdownMode);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return result;
        });

        await Assert.That(isWizard).IsTrue();
        await Assert.That(visible).IsTrue();
        // No Shutdown call and the mode untouched: closing the wizard hands over, it never exits.
        await Assert.That(mode).IsEqualTo(ShutdownMode.OnExplicitShutdown);
    }

    [Test]
    public async Task Closing_the_wizard_window_ends_the_close_wait() {
        await AvaloniaSession.DispatchAsync(async () => {
            var (wizard, _) = NewShell();
            var (desktop, _) = FakeClassicDesktopLifetime.Create();
            var window = AppUnderTest.ShowWizardWindow(desktop, wizard);
            Dispatcher.UIThread.RunJobs();

            var wait = AppUnderTest.WaitForWizardCloseAsync(wizard, CancellationToken.None);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            await wait.WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task A_shutdown_while_the_wizard_is_open_ends_the_close_wait() {
        await AvaloniaSession.DispatchAsync(async () => {
            var (wizard, _) = NewShell();
            using var cts = new CancellationTokenSource();

            var wait = AppUnderTest.WaitForWizardCloseAsync(wizard, cts.Token);
            await Task.Delay(20);
            await Assert.That(wait.IsCompleted).IsFalse();

            await cts.CancelAsync();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(20));
    }

    // ── the daemon enabler's row/ops identity derives from ResolveIdentity, never the claims
    // ── identity (ResolveConsentFlipIdentity stays literal, for TryConsume only) ──────

    [Test]
    public async Task The_daemon_enablers_row_reflects_ResolveIdentity_not_the_claims_identity() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("row-profile", "https://row.example:443", AuthProvider.None, "someone", []));
            // Deliberately different values: if the row-classification gate ever read the claims
            // identity instead, this server (which the claims identity does NOT name) would still
            // canonicalize fine and the row would read something other than NoServerConfigured.
            harness.Identity            = ("row-profile", "file:///etc/passwd", "row-daemon");
            harness.ConsentFlipIdentity = ("claims-profile", "https://claims.example", "claims-daemon");

            var graph  = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Daemon;
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await daemon.EnsureAsync(CancellationToken.None);

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.NoServerConfigured);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0); // refused before any status read

            return true;
        });
    }

    [Test]
    public async Task The_ops_factory_receives_the_row_identitys_daemon_name_not_the_claims_identitys() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("row-profile", "https://row.example:443", AuthProvider.None, "someone", []));
            harness.Identity            = ("row-profile", "https://row.example", "row-daemon");
            harness.ConsentFlipIdentity = ("claims-profile", "https://claims.example", "claims-daemon");
            harness.Cli.StatusBehavior  = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("row-daemon", true, "running", "/opt/kcap/kcap-daemon", "/opt/kcap/kcap-daemon",
                    111, 111, false, false));

            var canonical = ServerIdentity.Canonicalize("https://row.example")!;
            var evidence = new ObservedEvidence(
                Reachable: true, Capabilities: [ConsentFlipCoordinator.ConsentV3Capability], DaemonVersion: "1.0.0",
                ServerUrl: canonical, DaemonName: "row-daemon", Pid: 111, InstanceId: "instance-1", IdentityConsistent: true);
            var options = harness.Options() with { Observation = new WizardFixtures.FixedObservation(evidence) };

            harness.Ops.QueueGet(new ConsentPolicyDto("allow", 300, []));
            harness.Ops.QueuePutV2(true, null);
            harness.Claims.Arm(new ConsentFlipClaim("row-profile", canonical));

            var graph  = WizardComposition.BuildGraph(options);
            var daemon = graph.Daemon;
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await daemon.EnsureAsync(CancellationToken.None);
            await WizardFixtures.WaitUntilAsync(() => harness.Ops.PutV2Calls > 0, what: "the claim to apply");

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.AlreadyEnabled);
            await Assert.That(harness.OpsFactoryNames).Contains("row-daemon");
            await Assert.That(harness.OpsFactoryNames).DoesNotContain("claims-daemon");
            // The claim was found and PUT (matched against the mutation request, itself built from
            // the ROW identity) but TryConsume's own re-resolve — resolveIdentityUnderConfigLock,
            // i.e. ResolveConsentFlipIdentity, deliberately the SEPARATE claims identity — disagrees
            // with the claim's key, so the claim stays pending. This is the EXACT proof the claims
            // path stayed decoupled from the row identity, not a bug in this test.
            await Assert.That(harness.Claims.Pending().Select(c => c.Profile)).IsEquivalentTo(["row-profile"]);

            return true;
        });
    }

}

/// <summary>
/// The startup decisions that read real config: the single fresh resolution the graph is built on,
/// and the abandon cases (zero service mutation, gate still incomplete afterwards).
///
/// [NotInParallel]: the process-global headless session, since composing the wizard constructs
/// ReactiveUI ViewModels.
/// </summary>
[NotInParallel("AvaloniaSession")]
public class WizardStartupResolutionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
    static readonly WorkOSClient Workos = new(new PlainHttpClientFactory(), TimeProvider.System);

    const string ProfileName = "acme";

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));

    /// The post-wizard build must never reuse the startup-cached resolution: a config written WHILE
    /// the wizard was open is what the graph is built against.
    [Test]
    public async Task The_gate_is_re_resolved_from_the_config_on_every_call() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var (before, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System, CancellationToken.None);

        // What a committed wizard sign-in leaves behind: a real server plus its provider stamp.
        WriteConfig(SingleProfileConfig(new Profile {
            ServerUrl = "https://acme.example",
            AuthProvider = new AuthProviderStamp("none", "https://acme.example"),
        }));

        var (after, afterProfiles) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System, CancellationToken.None);

        await Assert.That(before).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)before).Reason).IsEqualTo(GateReason.InvalidServerUrl);
        await Assert.That(after).IsTypeOf<GateResult.Complete>();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(after)).IsFalse();
        // The graph identity comes from the SAME resolution the verdict was read off.
        await Assert.That(afterProfiles?.Resolution.ServerUrl).IsEqualTo("https://acme.example");
    }

    // ── App.ResolveWizardIdentity's own resolution rules ──────────────────────

    [Test]
    public async Task ResolveWizardIdentity_unreadable_config_is_null() {
        File.WriteAllText(ConfigPath, "{ this is not valid json");

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root, ProfileOverrides.None, NoLocalInstallation.Root);

        await Assert.That(identity).IsNull();
    }

    [Test]
    public async Task ResolveWizardIdentity_a_profile_with_an_invalid_server_is_null() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root, ProfileOverrides.None, NoLocalInstallation.Root);

        await Assert.That(identity).IsNull();
    }

    [Test]
    public async Task ResolveWizardIdentity_a_valid_profile_resolves_the_canonical_tuple() {
        WriteConfig(SingleProfileConfig(new Profile {
            ServerUrl = "https://acme.example",
            Daemon    = new DaemonSettings { Name = "acme-daemon" },
        }));

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root, ProfileOverrides.None, NoLocalInstallation.Root);

        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.Value.Profile).IsEqualTo(ProfileName);
        await Assert.That(identity.Value.Server).IsEqualTo(ServerIdentity.Canonicalize("https://acme.example"));
        await Assert.That(identity.Value.DaemonName).IsEqualTo("acme-daemon");
    }

    // An empty-name sentinel would dial the sanitized DEFAULT daemon socket — unreadable config must yield null.
    [Test]
    public async Task Unreadable_config_shows_the_daemon_enablers_not_ready_state_and_makes_no_status_or_ops_call() {
        File.WriteAllText(ConfigPath, "{ this is not valid json");

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));

            var options = harness.Options() with { ResolveIdentity = () => AppUnderTest.ResolveWizardIdentity(Config.Root, ProfileOverrides.None, NoLocalInstallation.Root) };
            var graph  = WizardComposition.BuildGraph(options);
            var daemon = graph.Daemon;
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(signIn.Satisfied).IsTrue(); // sign-in itself doesn't touch config.json

            await daemon.EnsureAsync(CancellationToken.None);

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.RequiresSignIn);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.OpsFactoryCalls).IsEqualTo(0);
            await Assert.That(harness.Lane.Requests).IsEmpty();

            return true;
        });
    }

    [Test]
    [Arguments("https://acme.example")] // valid URL, no token
    [Arguments("file:///tmp/x")]        // invalid, non-HTTP URL
    public async Task Abandoning_the_wizard_mutates_no_service_and_lands_on_the_carve_out_arm(string serverUrl) {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = serverUrl }));
        var configBefore = await File.ReadAllTextAsync(ConfigPath);

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var graph = WizardComposition.BuildGraph(harness.Options());

            // Abandon: the window closes without a single step being driven.
            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, graph.Install, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel(), TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            var (gate, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System, CancellationToken.None);

            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(configBefore);
            await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
            // The carve-out arm: auto-actions closed, which is also what suppresses the shim auto-offer.
            await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();

            return true;
        });
    }

    [Test]
    public async Task Closing_during_an_in_flight_pre_boundary_sign_in_writes_nothing() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "https://acme.example" }));
        var configBefore = await File.ReadAllTextAsync(ConfigPath);

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // still pre-boundary when the close arrives
                return new AuthResult.Committed(ProfileName, "https://acme.example:443", AuthProvider.None, null, []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Discover());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, graph.Install, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel(), TimeProvider.System)
                .WaitAsync(TimeSpan.FromSeconds(5));

            var (gate, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System, CancellationToken.None);

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();
            await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(configBefore);
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();

            return true;
        });
    }

    /// The production operation over a scripted /auth/config: a pasted server is ADOPTED (its
    /// server_url is written), which is what carries the gate to Complete — a non-adopting login
    /// would have failed with "profile is not configured for ...".
    [Test]
    public async Task A_pasted_server_is_adopted_and_the_arming_hook_runs_before_the_commit() {
        WriteConfig(new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });

        using var claimsRoot = new TempConfigRoot();
        var claims = new ConsentFlipClaims(claimsRoot.Root);
        var bridges = WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);
        using var handler = new StubAuthHandler();

        var operation = WizardComposition.BuildOperation(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), new PlainHttpClientFactory(handler),
            new AuthProxyClient(new HttpClient(handler, disposeHandler: false), TimeProvider.System), Github, Workos, ProfileName,
            bridges, claims, TimeProvider.System,
            spec => WizardSignInOperation.For(new OnboardingFacade(
                spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
                new RecordingBrowser(), spec.Picker, null, spec.Telemetry, spec.Endpoints, TimeProvider.System,
                spec.BeforeCommit), spec.Profile));

        var result = await operation(new ConnectIntent.Paste("https://acme.example"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var config = await AppConfig.LoadProfileConfig(Config.Root);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(config.Profiles[ProfileName].ServerUrl).IsEqualTo("https://acme.example");
        await Assert.That(config.Profiles[ProfileName].AuthProvider?.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([ProfileName]);
        await Assert.That(handler.Requests.Any(r => r.Contains("/auth/config"))).IsTrue();
    }

    /// Both discovery intents run the DISCOVERY leg (the auth proxy), not a per-server login.
    [Test]
    [Arguments("device")]
    [Arguments("browser")]
    public async Task Workos_discovery_routes_through_the_auth_proxy(string intentName) {
        using var claimsRoot = new TempConfigRoot();
        var claims = new ConsentFlipClaims(claimsRoot.Root);
        var bridges = WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);
        using var handler = new StubAuthHandler { Status = HttpStatusCode.ServiceUnavailable };
        ConnectIntent intent = intentName == "device"
            ? new ConnectIntent.Discover(ForceDevice: true)
            : new ConnectIntent.Discover();

        var operation = WizardComposition.BuildOperation(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), new PlainHttpClientFactory(handler),
            new AuthProxyClient(new HttpClient(handler, disposeHandler: false), TimeProvider.System), Github, Workos, ProfileName,
            bridges, claims, TimeProvider.System,
            s => WizardSignInOperation.For(new OnboardingFacade(
                s.Root, s.TokenStore, s.HttpFactory, s.Proxy, s.GitHub, s.WorkOS, s.Progress,
                new RecordingBrowser(), s.Picker, null, s.Telemetry, s.Endpoints, TimeProvider.System, s.BeforeCommit), s.Profile));

        var result = await operation(intent, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(handler.Requests.Any(r => r.Contains("/config"))).IsTrue();
    }

    /// Answers every GET with an auth-config body; the discovery rows flip Status so the proxy leg
    /// fails fast instead of opening a browser.
    sealed class StubAuthHandler : HttpMessageHandler {
        public readonly List<string> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(Status) {
                Content = new StringContent("""{"provider":"None"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
