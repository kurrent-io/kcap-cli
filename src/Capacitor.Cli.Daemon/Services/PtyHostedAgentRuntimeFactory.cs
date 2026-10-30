using System.Collections.Frozen;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Pty;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for the interactive PTY-backed CLIs (Claude, Codex):
/// builds the <see cref="LauncherContext"/>, runs <see cref="IHostedAgentLauncher.Prepare"/> then
/// <see cref="IHostedAgentLauncher.BuildArgs"/>, assembles the daemon-injected env vars, spawns the
/// PTY and wraps it in a <see cref="PtyHostedAgentRuntime"/>. A prompt that would otherwise sit on
/// the command line is withheld from the args and pasted through the runtime after spawn.
///
/// <see cref="CodexHooksNotInstalledException"/> from <c>Prepare</c> propagates to the orchestrator,
/// which maps it to <c>LaunchFailed</c> and worktree cleanup. Any other <c>Prepare</c> failure is
/// logged and swallowed so a settings-overlay glitch never blocks a launch.
/// </summary>
/// <param name="home">The daemon's home, against which an account directory is told apart from the
/// vendor's built-in one.</param>
internal sealed partial class PtyHostedAgentRuntimeFactory(
        IHostedAgentLauncher                  launcher,
        IPtyProcessFactory                    ptyFactory,
        ILogger<PtyHostedAgentRuntimeFactory> logger,
        TimeProvider                          time,
        UserHome                              home
    ) : IHostedAgentRuntimeFactory {
    public string Vendor            => launcher.Vendor;
    public bool   SupportsUnattended => launcher.SupportsUnattended;
    public bool   SupportsPrReview   => launcher.SupportsPrReview;
    public bool   SupportsBorrowedReviewFlow => launcher.SupportsBorrowedReviewFlow;
    public bool   SupportsRestartResume(string? runtimeTransport) => launcher.SupportsResume;
    public bool   SupportsReviewerRestartResume(string? runtimeTransport, Core.LocalIpc.WorkLocation work) =>
        work == Core.LocalIpc.WorkLocation.OwnedWorktree && launcher.SupportsReviewerResume;

    /// <summary>The launcher passes no prompt argument for an empty prompt and nothing is pasted, so the agent
    /// reopens its session and waits.</summary>
    public bool ResumesIdleWithoutPrompt(string? runtimeTransport) => SupportsRestartResume(runtimeTransport);

    public string? BorrowedReviewContainment => launcher.BorrowedReviewContainment;

    /// <summary>PTY factories delegate reviewer-model resolution to their launcher-owned policy — the
    /// launcher (Claude, Codex) owns its accepted aliases/ids and canonical/equivalence behavior.</summary>
    public IReviewerModelResolver? ReviewerModelResolver => launcher.ReviewerModelResolver;

    public string CliPath => launcher.CliPath;

    public bool IsAvailable() => launcher.IsAvailable();

    /// <remarks>
    /// <paramref name="ct"/> is accepted for interface parity with <see cref="IHostedAgentRuntimeFactory"/>
    /// but is not observed on this path — <c>Prepare</c>, <c>BuildArgs</c>, and the PTY spawn are all
    /// synchronous/uncancellable, matching the pre-refactor behavior this factory was extracted from
    /// (not a regression introduced here). A caller should not assume cancelling <paramref name="ct"/>
    /// aborts an in-flight PTY launch.
    /// </remarks>
    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var launcherCtx = new LauncherContext(
            AgentId: ctx.AgentId,
            SourceRepoPath: ctx.SourceRepoPath,
            Worktree: ctx.Worktree,
            Prompt: ctx.Prompt,
            Model: ctx.Model,
            Effort: ctx.Effort,
            Tools: ctx.Tools,
            IsReview: ctx.IsReview,
            IsReviewFlow: ctx.IsReviewFlow,
            Review: ctx.Review,
            // The review-launch MCP server command must be the kcap binary (ctx.CapacitorPath),
            // NOT the vendor CLI (launcher.CliPath) — the review agent runs `kcap mcp review` to
            // talk to the review MCP tools.
            ReviewLaunch: ctx.IsReview && ctx.Review is { } reviewArgs
                ? await ReviewLaunchBuilder.BuildAsync(ctx.Vendor, ctx.CapacitorPath, ctx.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
                : null,
            Account: ctx.Account
        ) {
            McpAllowlist = ctx.McpAllowlist,
            Work           = ctx.Work,
            CodexPosture   = ctx.CodexPosture,
            PermissionMode = ctx.PermissionMode,
            PinnedSessionId = ctx.PinnedSessionId,
            ResumeSessionId = ctx.ResumeSessionId
        };

        var owned = launcherCtx.Work == Core.LocalIpc.WorkLocation.OwnedWorktree;
        var before = owned ? WorktreeManager.StampDaemonPaths(launcherCtx.Worktree.Path) : null;
        try {
            launcher.Prepare(launcherCtx);
        } catch (UnattendedReviewPolicyException) {
            throw;
        } catch (CodexHooksNotInstalledException) {
            // Propagate — the orchestrator maps this to LaunchFailed + worktree cleanup.
            throw;
        } catch (Exception ex) {
            LogPrepareSoftFailure(ex, ctx.AgentId);
        } finally {
            if (owned) WorktreeManager.RecordDaemonBaseline(launcherCtx.Worktree.Path, before);
        }

        var launchArgs = launcher.BuildArgs(launcherCtx);

        string? launchPrompt = null;
        if (launcherCtx.Prompt is { Length: > 0 } prompt
            && launchArgs.Args.Contains(prompt)
            && !CommandLinePrompt.StaysInline(prompt, launchArgs.Args)) {
            launcherCtx  = launcherCtx with { Prompt = null };
            launchArgs   = launcher.BuildArgs(launcherCtx);
            launchPrompt = prompt;
            LogPromptPastedAfterSpawn(ctx.AgentId, prompt.Length);
        }

        var args          = launchArgs.Args;
        var mcpConfigPath = launchArgs.McpConfigPath;

        var env = new Dictionary<string, string>();

        // A private agent gets what a --private local spawn gets: nothing that ties its session to
        // the agent on the server.
        if (!ctx.IsPrivate) {
            env[HostedAgent.RenderedVar] = HostedAgent.Rendered;
            env[HostedAgent.AgentIdVar]  = ctx.AgentId;
            HostedLaunchMarkers.ApplyOverlay(env, ctx);
        }

        // Phase B (D4 §6.4(3)): stamp the daemon-identity markers so a restarted daemon's
        // OrphanReaper env-marker scan can reap a recordless survivor of a PRIOR incarnation of THIS
        // daemon (same KCAP_DAEMON_ID, older KCAP_DAEMON_EPOCH) — and never touch another daemon's
        // child or a live agent of the current incarnation.
        if (!string.IsNullOrEmpty(ctx.DaemonId))    env["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) env["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) {
            env[ProfileOverrides.UrlVar] = ctx.ServerUrl;
        }

        // Tell the spawned Claude's permission-request hook where to find this daemon's local
        // SignalR bridge. Bypasses Cloudflare's HTTP timeout on the server's
        // /hooks/permission-request long-poll. CLI falls back to KCAP_URL if this var is absent
        // (e.g. older CLI builds).
        if (!ctx.IsPrivate && !string.IsNullOrEmpty(ctx.DaemonBridgeUrl)) {
            env[HostedAgent.BridgeUrlVar] = ctx.DaemonBridgeUrl;
        }

        if (ctx.IsReview && ctx.Review is { } reviewEnv) {
            env["KCAP_REVIEW_PR"] = reviewEnv.PrNumber.ToString();
        }

        var removeEnv = ctx.Account is { } account
            ? VendorHomeEnv.Apply(env, ctx.Vendor, account, home)
            : FrozenSet<string>.Empty;

        // Read before the spawn so a throw cannot orphan the PTY. Gates the multi-CR submit spray; see
        // PtyHostedAgentRuntime.SubmitAsync.
        var disablesApprovalPrompts = launcher.DisablesApprovalPrompts(launcherCtx);

        var pty     = ptyFactory.Spawn(launcher.CliPath, args, ctx.Worktree.Path, env, ctx.Cols, ctx.Rows, removeEnv);
        var runtime = new PtyHostedAgentRuntime(ctx.Vendor, pty, time, disablesApprovalPrompts, logger,
            readyOnBracketedPaste: OperatingSystem.IsWindows());

        if (launchPrompt is not null) {
            runtime.DeliverLaunchPrompt(launchPrompt);
            _ = LogLaunchPromptOutcomeAsync(runtime.LaunchPromptDelivery!, ctx.AgentId);
        }

        return new HostedRuntimeStart(runtime, mcpConfigPath);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Launcher Prepare soft-failure for agent {AgentId} (continuing)")]
    partial void LogPrepareSoftFailure(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId} receives its {Length}-character prompt through the PTY after spawn")]
    partial void LogPromptPastedAfterSpawn(string agentId, int length);

    /// <summary>An agent that never received its task leaves no other trace, so every withdrawn or
    /// blind delivery is logged here.</summary>
    async Task LogLaunchPromptOutcomeAsync(Task<LaunchPromptOutcome> delivery, string agentId) {
        LaunchPromptOutcome outcome;

        try {
            outcome = await delivery.ConfigureAwait(false);
        } catch (Exception ex) {
            LogLaunchPromptFaulted(ex, agentId);

            return;
        }

        switch (outcome) {
            case LaunchPromptOutcome.Delivered:             LogLaunchPromptDelivered(agentId); break;
            case LaunchPromptOutcome.DeliveredAfterMaxWait: LogLaunchPromptPastedBlind(agentId, PtyHostedAgentRuntime.LaunchPromptMaxWait.TotalSeconds); break;
            case LaunchPromptOutcome.AgentExited:           LogLaunchPromptAgentExited(agentId); break;
            case LaunchPromptOutcome.WriteFailed:           LogLaunchPromptWriteFailed(agentId); break;
            default:                                        LogLaunchPromptWithdrawn(agentId, outcome); break;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId} received its launch prompt")]
    partial void LogLaunchPromptDelivered(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} never settled ready for input; its launch prompt was pasted after {Seconds}s")]
    partial void LogLaunchPromptPastedBlind(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} exited before it received its launch prompt")]
    partial void LogLaunchPromptAgentExited(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId}'s launch prompt could not be written to its PTY")]
    partial void LogLaunchPromptWriteFailed(string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId}'s launch prompt was withdrawn ({Outcome})")]
    partial void LogLaunchPromptWithdrawn(string agentId, LaunchPromptOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId}'s launch prompt delivery faulted")]
    partial void LogLaunchPromptFaulted(Exception ex, string agentId);
}
