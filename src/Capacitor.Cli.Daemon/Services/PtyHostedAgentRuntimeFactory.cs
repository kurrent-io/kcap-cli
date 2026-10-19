using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Pty;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for the interactive PTY-backed CLIs (Claude, Codex).
/// Moved verbatim from the body of <see cref="AgentOrchestrator.HandleLaunchAgent"/> (Task
/// 10): builds the <see cref="LauncherContext"/> (including the review-launch
/// <see cref="ReviewLaunchBuilder.BuildAsync"/> call), runs <see cref="IHostedAgentLauncher.Prepare"/>
/// then <see cref="IHostedAgentLauncher.BuildArgs"/>, assembles the daemon-injected env vars, spawns
/// the PTY via the injected <see cref="IPtyProcessFactory"/> (the same instance
/// <c>SpyPtyProcessFactory</c> asserts against in the vendor-routing tests), and wraps the result in
/// a <see cref="PtyHostedAgentRuntime"/>.
///
/// <see cref="CodexHooksNotInstalledException"/> from <c>Prepare</c> is intentionally left
/// unhandled here — it propagates to the orchestrator, which maps it to <c>LaunchFailed</c> +
/// worktree cleanup. Any other <c>Prepare</c> failure is soft-logged and swallowed so a
/// settings-overlay glitch never blocks launch (same contract as before the refactor,
/// <c>AgentOrchestrator.LogPrepareSoftFailure</c>).
/// </summary>
/// <param name="promptRoot">Where launch prompts are written. It must be owner-only, which the
/// daemon's state directory is and a shared temp directory is not.</param>
internal sealed partial class PtyHostedAgentRuntimeFactory(
        IHostedAgentLauncher                  launcher,
        IPtyProcessFactory                    ptyFactory,
        ILogger<PtyHostedAgentRuntimeFactory> logger,
        TimeProvider                          time,
        string                                promptRoot
    ) : IHostedAgentRuntimeFactory {
    public string Vendor            => launcher.Vendor;
    public bool   SupportsUnattended => launcher.SupportsUnattended;
    public bool   SupportsPrReview   => launcher.SupportsPrReview;
    public bool   SupportsBorrowedReviewFlow => launcher.SupportsBorrowedReviewFlow;
    public bool   SupportsRestartResume(string? runtimeTransport) => launcher.SupportsResume;
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
                : null
        ) {
            McpAllowlist = ctx.McpAllowlist,
            Work           = ctx.Work,
            CodexPosture   = ctx.CodexPosture,
            PermissionMode = ctx.PermissionMode,
            PinnedSessionId = ctx.PinnedSessionId,
            ResumeSessionId = ctx.ResumeSessionId
        };

        try {
            launcher.Prepare(launcherCtx);
        } catch (UnattendedReviewPolicyException) {
            throw;
        } catch (CodexHooksNotInstalledException) {
            // Propagate — the orchestrator maps this to LaunchFailed + worktree cleanup.
            throw;
        } catch (Exception ex) {
            LogPrepareSoftFailure(ex, ctx.AgentId);
        }

        var launchArgs = launcher.BuildArgs(launcherCtx);

        string? promptFile = null;
        if (launcherCtx.Prompt is { Length: > 0 } prompt
            && launchArgs.Args.Contains(prompt)
            && !StaysInline(prompt, launchArgs.Args)) {
            promptFile = PromptFile.Write(promptRoot, ctx.AgentId, prompt);
            try {
                launcherCtx = launcherCtx with { PromptFile = promptFile };
                launchArgs  = launcher.BuildArgs(launcherCtx);
            } catch {
                PromptFile.Delete(promptFile);
                throw;
            }
            LogPromptMovedToFile(ctx.AgentId, prompt.Length);
        }

        var args          = launchArgs.Args;
        var mcpConfigPath = launchArgs.McpConfigPath;

        var env = new Dictionary<string, string>();

        // A private agent gets what a --private local spawn gets: nothing that ties its session to
        // the agent on the server.
        if (!ctx.IsPrivate) {
            env[HostedAgent.RenderedVar] = HostedAgent.Rendered;
            env[HostedAgent.AgentIdVar]  = ctx.AgentId;
            HostedLaunchMarkers.Apply(env, ctx.IsReviewFlow, ctx.FlowRole);
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

        IPtyProcess pty;
        try {
            pty = ptyFactory.Spawn(launcher.CliPath, args, ctx.Worktree.Path, env, ctx.Cols, ctx.Rows);
        } catch {
            if (promptFile is not null) PromptFile.Delete(promptFile);
            throw;
        }

        // Gate the multi-CR submit spray on whether this launch turned off approval prompts — the
        // launcher is the authority (it set the flags). See PtyHostedAgentRuntime.SubmitAsync.
        var runtime = new PtyHostedAgentRuntime(ctx.Vendor, pty, time, launcher.DisablesApprovalPrompts(launcherCtx), promptFile);

        return new HostedRuntimeStart(runtime, mcpConfigPath);
    }

    /// <summary>A slash command stays inline unless it is too long for a Windows command line, where
    /// the spawn would fail outright.</summary>
    static bool StaysInline(string prompt, IReadOnlyList<string> args) =>
        PromptFile.IsSlashCommand(prompt) && !(OperatingSystem.IsWindows() && PromptFile.Overflows(args));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Launcher Prepare soft-failure for agent {AgentId} (continuing)")]
    partial void LogPrepareSoftFailure(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent {AgentId} reads its {Length}-character prompt from a file")]
    partial void LogPromptMovedToFile(string agentId, int length);
}
