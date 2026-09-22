using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// GATED live certification of the unattended Pi reviewer against a REAL <c>pi --mode rpc</c> child,
/// driven exactly as a <c>start_review_flow(kind: "code-review", vendor: "pi")</c> launch drives it:
/// a real <see cref="PiRpcHostedAgentRuntimeFactory"/> with no seams, a real <c>kcap mcp flow-result</c>
/// child Pi's own extension spawns as its result channel, and a real POST to the live server named by
/// <c>KCAP_URL</c>.
///
/// <para><b>What this certifies.</b> A seeded-defect, multi-round shape, mirroring
/// <see cref="Antigravity.AntigravityReviewerLiveCertTests"/>: round 1 is asked to review a file that
/// contains one planted defect and must report <c>submit_review_result(kind: "findings")</c> for its
/// own round token; the defect is then fixed ON DISK and round 2 — the SAME long-lived reviewer,
/// resumed with a fresh user message rather than a fresh process (Pi's whole point, unlike agy's
/// exec-per-turn shape) — must report <c>kind: "clean"</c> for its own, different token. A reviewer
/// that never actually reads the file, or that reports a fixed verdict regardless of what changed,
/// passes neither round honestly. At least one <c>read_file</c> tool call is required as independent
/// evidence the verdict came from the contained tools and not from the prompt text alone.</para>
///
/// <para><b>Gated</b> behind <c>KCAP_PI_REVIEWER_LIVE=1</c>: CI has no <c>pi</c> binary and no
/// authenticated provider, and each case spends real model turns plus a real HTTP round trip. Requires
/// <c>pi</c> on <c>PATH</c> (or <c>KCAP_PI_PATH</c>) — the harness records that build as this daemon's
/// minimum, exactly as enabling the reviewer does — a real <c>kcap</c> on <c>PATH</c> (or
/// <c>KCAP_PATH</c>), and <c>KCAP_URL</c> pointed at a reachable server this machine has already run
/// <c>kcap login</c> against.</para>
/// </summary>
public class PiReviewerLiveCertTests {
    const string GateEnvVar = "KCAP_PI_REVIEWER_LIVE";

    /// <summary>Bounded on purpose, and generously: a real model turn that reads a file and calls a
    /// tool is slower than a bare reply, but "never reported" and "skipped or inconclusive" must stay
    /// distinguishable, or a broken reviewer looks exactly like a test that did not run.</summary>
    static readonly TimeSpan RoundBudget  = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    const string SubjectFile = "subject.txt";

    /// <summary>
    /// The gate. <see cref="Skip"/> is the FIRST statement executed on every path, so an ungated run
    /// costs a process-environment read and nothing else.
    ///
    /// <para>The <c>KCAP_URL</c> check fails LOUDLY rather than skipping: once an operator has asked
    /// for a live run, an unset server would present downstream as every round timing out waiting for
    /// a <c>submit_review_result</c> that can never arrive — the result channel has nowhere to POST —
    /// which names the wrong culprit unless the harness says so first.</para>
    /// </summary>
    static string Gate() {
        Skip.Unless(Environment.GetEnvironmentVariable(GateEnvVar) == "1",
            $"Gated live certification of the unattended Pi reviewer — set {GateEnvVar}=1 to run "
          + "(spends real pi turns and a real submit_review_result POST; needs `pi` on PATH, "
          + "authenticated for a provider, a real `kcap` on PATH, and "
          + $"{ProfileOverrides.UrlVar}=<reachable kcap server> with `kcap login` already done "
          + "against it).");

        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Pi reviewer is POSIX-only: its per-launch directory holds the reviewer's transcript "
          + "and manifest and cannot be created owner-only on Windows.");

        var url = Environment.GetEnvironmentVariable(ProfileOverrides.UrlVar);

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"{GateEnvVar}=1 but {ProfileOverrides.UrlVar} is unset. The reviewer's result channel "
              + "has no server to POST submit_review_result to, so every round would time out waiting "
              + $"for a verdict that can never arrive. Export {ProfileOverrides.UrlVar} and re-run.");

        return url;
    }

    /// <summary>
    /// Round 1 reviews a seeded defect and must report findings for its own round token; the defect is
    /// fixed on disk and round 2 — the same reviewer, same process — must report clean for a different
    /// token. See the class doc for what each half of this proves.
    /// </summary>
    [Test]
    public async Task Cert_SeededDefectFindingsThenFixedOnTheSameReviewerReportsClean() {
        var serverUrl = Gate();

        using var repo    = GitRepo.Create("pircert");
        using var daemons = new TempDaemonStore();

        var subjectPath = repo.CreateFile(SubjectFile, "int Add(int a, int b) => a - b;\n");

        var config = new DaemonConfig {
            PiPath = Environment.GetEnvironmentVariable("KCAP_PI_PATH") is { Length: > 0 } path
                ? path : "pi",
            PiUnattendedReviewerEnabled  = true,
            PiReviewerTurnTimeoutSeconds = (int)RoundBudget.TotalSeconds,
            Name                         = "pi-reviewer-live-cert",
            DaemonEpoch                  = "cert-" + Guid.NewGuid().ToString("N")[..8],
            Binaries                     = BinaryProbe.FromEnvironment(),
            Store                        = daemons.Store
        };

        // Seeded through the DAEMON's own path, not a hand-written record — see
        // AntigravityReviewerLiveCertTests.LiveHarness.Create for why: production records the minimum
        // from the consent event at startup, and this harness stands in for a daemon whose operator has
        // just enabled the reviewer.
        DaemonRunner.SeedReviewerAffirmation(
            config.Store.StateDirectory(config.Name), DaemonRunner.PiVendor,
            enabled: true, config.PiPath, config.Binaries);

        var installed = await LogPiVersionAsync(config.PiPath);
        await ReviewerCertFloor.RequireAtOrAboveFloorAsync(installed, PiReviewerCapability.VerifiedFloor);

        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        // No seams: binaryExists/resolveVersion left to production, and processSource left to the
        // factory's default real PiRpcProcess spawn — this cert exists to judge the INSTALLED pi, so
        // seaming any of that would certify a build the gate would have refused.
        var factory = new PiRpcHostedAgentRuntimeFactory(config, liveLoggerFactory, TimeProvider.System);

        var round1Token = "r1-" + Guid.NewGuid().ToString("N")[..8];

        var ctx = new RuntimeStartContext(
            AgentId: "pi-reviewer-cert-" + Guid.NewGuid().ToString("N")[..8],
            Vendor: "pi",
            SourceRepoPath: repo.Path,
            Worktree: new WorktreeInfo(Path: repo.Path, Branch: "", SourceRepo: repo.Path),
            Prompt: RoundPrompt(round1Token, SubjectFile),
            Model: null, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: true, Review: null,
            Cols: 80, Rows: 24,
            ServerUrl: serverUrl,
            DaemonBridgeUrl: null,
            CapacitorPath: Environment.GetEnvironmentVariable("KCAP_PATH") is { Length: > 0 } kcap
                ? kcap : "kcap",
            DaemonId: config.Name,
            DaemonEpoch: config.DaemonEpoch!);

        var start     = await factory.StartAsync(ctx, CancellationToken.None).WaitAsync(ReadyTimeout);
        var runtime   = start.Runtime;
        var envelopes = start.Transcript!.Envelopes;

        try {
            var round1 = await CollectUntilSubmitAsync(envelopes, round1Token, RoundBudget);

            Report("round1", round1);

            await Assert.That(round1.Submit).IsNotNull()
                .Because($"no submit_review_result arrived for round token {round1Token} within "
                       + $"{RoundBudget} — the reviewer never reported, or reported against a "
                       + $"different token: {round1.FailureNote}");

            var (_, kind1) = ParseSubmitArgs(round1.Submit!.Value.ToolInputJson);

            await Assert.That(kind1).IsEqualTo("findings")
                .Because("the seeded defect (subtraction where the file is named Add) must be reported "
                       + $"as findings, not: {round1.Submit.Value.ToolInputJson}");

            // Let the turn fully settle before handing the reviewer a second prompt — Pi is one
            // long-lived process for the whole launch, and overlapping a still-in-flight turn with a
            // new one is not a shape any real review flow produces.
            using (var idleCts = new CancellationTokenSource(RoundBudget))
                await runtime.WaitForTurnIdleAsync(idleCts.Token);

            // Fix the defect ON DISK before asking for round 2 — a reviewer that reports clean
            // regardless of the file's actual contents must fail this, not round 1.
            await File.WriteAllTextAsync(subjectPath, "int Add(int a, int b) => a + b;\n");

            var round2Token = "r2-" + Guid.NewGuid().ToString("N")[..8];
            await runtime.SendUserInputAsync(RoundPrompt(round2Token, SubjectFile));

            var round2 = await CollectUntilSubmitAsync(envelopes, round2Token, RoundBudget);

            Report("round2", round2);

            await Assert.That(round2.Submit).IsNotNull()
                .Because($"no submit_review_result arrived for round token {round2Token} within "
                       + $"{RoundBudget}: {round2.FailureNote}");

            var (_, kind2) = ParseSubmitArgs(round2.Submit!.Value.ToolInputJson);

            await Assert.That(kind2).IsEqualTo("clean")
                .Because("the defect was fixed on disk before round 2 — a genuinely clean file must "
                       + $"not be reported as findings: {round2.Submit.Value.ToolInputJson}");

            await Assert.That(round1.SawReadFile || round2.SawReadFile).IsTrue()
                .Because("the reviewer must actually use its contained read_file tool at least once — "
                       + "a verdict with no read_file call is not evidence the contained tools work");
        } finally {
            try {
                await runtime.RequestGracefulStopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await runtime.WaitForExitAsync(TimeSpan.FromSeconds(15));
            } catch (Exception ex) {
                Console.WriteLine($"[pi-reviewer-live] graceful stop did not complete cleanly: {ex.Message}");
            }

            await runtime.DisposeAsync();
        }
    }

    // ── round mechanics ──────────────────────────────────────────────────────────────────────────

    static string RoundPrompt(string roundToken, string relativeFile) =>
        $"""
        Round token: {roundToken}

        Review {relativeFile} in the repository root for correctness defects. Read it with read_file
        before you answer — do not guess at its contents. When you are done, call
        submit_review_result exactly once, with round_token="{roundToken}" and kind="findings" (with a
        findings description of the defect) if the file has one, or kind="clean" if it does not.
        """;

    readonly record struct SubmitOutcome(
        AcpEventEnvelope? Submit, bool SawReadFile, int EnvelopeCount, string? FailureNote);

    /// <summary>
    /// Drains <paramref name="envelopes"/> until a <c>submit_review_result</c> tool call carrying
    /// <paramref name="roundToken"/> arrives, a turn-failed <c>system_note</c> arrives (an environment
    /// fault that would otherwise burn the whole timeout as opaque dead air), or <paramref
    /// name="timeout"/> elapses. Tracks every <c>read_file</c> tool call seen along the way, whether or
    /// not this round's own submit matches.
    /// </summary>
    static async Task<SubmitOutcome> CollectUntilSubmitAsync(
            ChannelReader<AcpEventEnvelope> envelopes, string roundToken, TimeSpan timeout) {
        var sawReadFile = false;
        var count       = 0;

        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await envelopes.WaitToReadAsync(timeoutCts.Token)) {
                while (envelopes.TryRead(out var env)) {
                    count++;

                    if (env.Kind != AcpEventKind.ToolCall) continue;

                    if (env.ToolName == "read_file") sawReadFile = true;

                    if (env.ToolName == "submit_review_result") {
                        var (token, _) = ParseSubmitArgs(env.ToolInputJson);
                        if (token == roundToken) return new(env, sawReadFile, count, null);
                    }

                    if (env is { Kind: AcpEventKind.SystemNote, Text: { } note }
                            && note.StartsWith("Pi turn failed", StringComparison.Ordinal))
                        return new(null, sawReadFile, count, note);
                }
            }
        } catch (OperationCanceledException) {
            // Timed out waiting for the matching submit — fall through and report what was observed.
        }

        return new(null, sawReadFile, count, null);
    }

    static (string? RoundToken, string? Kind) ParseSubmitArgs(string? toolInputJson) {
        if (string.IsNullOrEmpty(toolInputJson)) return (null, null);

        try {
            using var doc  = JsonDocument.Parse(toolInputJson);
            var       root = doc.RootElement;

            return (
                root.TryGetProperty("round_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("kind", out var k) ? k.GetString() : null);
        } catch (JsonException) {
            return (null, null);
        }
    }

    static void Report(string label, SubmitOutcome outcome) =>
        Console.WriteLine(
            $"[pi-reviewer-live:{label}] envelopes={outcome.EnvelopeCount} read_file={outcome.SawReadFile} "
          + $"submit={(outcome.Submit is { } e ? e.ToolInputJson : "(none)")} note={outcome.FailureNote}");

    /// <summary>Records the installed build for the log AND feeds
    /// <see cref="ReviewerCertFloor.RequireAtOrAboveFloorAsync"/> — a cert result is meaningless
    /// without knowing which build it ran against.</summary>
    static async Task<string> LogPiVersionAsync(string piPath) {
        try {
            using var process = Process.Start(new ProcessStartInfo(piPath, ["--version"]) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            });

            if (process is null) {
                Console.WriteLine("[pi-reviewer-live] pi --version: could not start process");
                return "";
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var installed = stdout.Trim();
            Console.WriteLine($"[pi-reviewer-live] pi --version: {installed}{stderr.Trim()}");
            return installed;
        } catch (Exception ex) {
            Console.WriteLine($"[pi-reviewer-live] pi --version failed: {ex.Message}");
            return "";
        }
    }
}
