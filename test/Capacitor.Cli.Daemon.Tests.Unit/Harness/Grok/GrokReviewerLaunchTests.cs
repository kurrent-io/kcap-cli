using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

/// <summary>The Grok review launch: its gate, the tool clamp its <c>session/new</c> carries, and the
/// environment that keeps the user's other harnesses out of it.</summary>
public class GrokReviewerLaunchTests {
    const string InstalledVersion = "1.0.52";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    DaemonConfig EnabledConfig() {
        var config = new DaemonConfig { GrokUnattendedReviewerEnabled = true, Store = Daemons.Store, Name = "test-daemon", DaemonEpoch = "epoch-1" };
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.Grok.Vendor).Affirm(InstalledVersion);
        return config;
    }

    System.Diagnostics.ProcessStartInfo Psi(bool isReviewFlow) =>
        AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Grok, EnabledConfig(), Ctx(isReviewFlow), resolveGeminiVersion: _ => InstalledVersion);

    static RuntimeStartContext Ctx(bool isReviewFlow) =>
        new(AgentId: "agent-1", Vendor: "grok", SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
            Model: null, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
            Cols: 80, Rows: 24, ServerUrl: isReviewFlow ? "http://kcap.test" : null,
            DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    [Test]
    public async Task A_review_launch_clamps_the_tools_and_still_asks() {
        var meta = GrokReviewerLaunch.SessionNewMeta(isReviewFlow: true);

        await Assert.That(meta["yoloMode"]!.GetValue<bool>()).IsFalse();
        var tools      = meta["agentProfile"]!["tools"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        var disallowed = meta["agentProfile"]!["disallowedTools"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        await Assert.That(tools).DoesNotContain("search_replace");
        await Assert.That(tools).DoesNotContain("write");
        await Assert.That(disallowed).Contains("search_replace");
        await Assert.That(disallowed).Contains("write");
        await Assert.That(disallowed).Contains("Agent");
    }

    [Test]
    public async Task An_interactive_launch_keeps_the_users_tools() {
        var meta = GrokReviewerLaunch.SessionNewMeta(isReviewFlow: false);

        await Assert.That(meta["yoloMode"]!.GetValue<bool>()).IsFalse();
        await Assert.That(meta.ContainsKey("agentProfile")).IsFalse();
    }

    [Test]
    public async Task A_review_launch_turns_off_claude_and_cursor_compat_and_subagents() {
        var review = Psi(isReviewFlow: true);

        await Assert.That(review.Environment["GROK_CLAUDE_MCPS_ENABLED"]).IsEqualTo("false");
        await Assert.That(review.Environment["GROK_CLAUDE_HOOKS_ENABLED"]).IsEqualTo("false");
        await Assert.That(review.Environment["GROK_CURSOR_MCPS_ENABLED"]).IsEqualTo("false");
        await Assert.That(review.Environment["GROK_SUBAGENTS"]).IsEqualTo("0");
    }

    /// <summary>An interactive launch behaves as the user's own session does: none of it is imposed.</summary>
    [Test]
    public async Task An_interactive_launch_keeps_the_users_compat_sources() {
        var interactive = Psi(isReviewFlow: false);

        foreach (var key in GrokReviewerLaunch.ReviewerEnvironment.Keys)
            await Assert.That(interactive.Environment.TryGetValue(key, out var value) ? value : null)
                .IsEqualTo(Environment.GetEnvironmentVariable(key));
    }

    [Test]
    public async Task A_review_launch_below_the_recorded_minimum_is_refused() {
        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Grok, EnabledConfig(), Ctx(isReviewFlow: true), resolveGeminiVersion: _ => "1.0.40"))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("grok_reviewer_version_below_minimum");
    }

    [Test]
    public async Task A_review_launch_is_refused_on_a_daemon_that_disabled_it() {
        var disabled = new DaemonConfig { GrokUnattendedReviewerEnabled = false };

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Grok, disabled, Ctx(isReviewFlow: true)))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("grok_unattended_reviewer_disabled");
    }
}
