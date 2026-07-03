using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Runtime-selection seam (AI-684 Task 10): one implementation per vendor family, chosen by
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> via <c>cmd.Vendor</c> instead of the orchestrator
/// itself building the vendor-specific runtime inline. <see cref="PtyHostedAgentRuntimeFactory"/>
/// wraps an <see cref="IHostedAgentLauncher"/> + <see cref="Pty.IPtyProcessFactory"/> for the
/// interactive CLIs (Claude, Codex); <see cref="AcpHostedAgentRuntimeFactory"/> spawns
/// <c>cursor-agent acp</c> and speaks ACP JSON-RPC for Cursor.
/// </summary>
internal interface IHostedAgentRuntimeFactory {
    /// <summary>Vendor token this factory handles ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>
    /// Reports whether this vendor's CLI resolves to an executable that looks installed. Used at
    /// daemon startup to build the vendor list advertised over <c>DaemonConnect</c>.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Whether this vendor can host a fully UNATTENDED agent (<see cref="LaunchKind.ReviewFlow"/>).
    /// The orchestrator refuses an unattended launch for a vendor that returns <c>false</c>.
    /// </summary>
    bool SupportsUnattended { get; }

    /// <summary>
    /// Prepares and starts the hosted runtime for this launch. Throws
    /// <see cref="CodexHooksNotInstalledException"/> for the orchestrator to map to a
    /// <c>LaunchFailed</c> with worktree cleanup; any other exception is likewise mapped to
    /// <c>LaunchFailed</c> (failed-launch path). Returns the started runtime plus any temp
    /// mcp-config path the orchestrator must record on <see cref="AgentInstance"/> for cleanup.
    /// </summary>
    Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct);
}

/// <summary>Result of <see cref="IHostedAgentRuntimeFactory.StartAsync"/>: the started runtime and
/// any temp mcp-config path the orchestrator must record on <see cref="AgentInstance.McpConfigPath"/>
/// so it's cleaned up alongside the agent (PTY launchers only — the ACP factory returns null).</summary>
internal readonly record struct HostedRuntimeStart(IHostedAgentRuntime Runtime, string? McpConfigPath);

/// <summary>
/// Everything a runtime factory needs to prepare and start a hosted agent for one launch. Built by
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> from the inbound <c>LaunchAgentCommand</c> plus
/// daemon-local state (worktree, review-launch info, permission-bridge URL) after the pre-flight
/// guards (vendor known, unattended support, repo allowed, worktree created) have already passed.
/// </summary>
/// <param name="CapacitorPath">
/// Absolute path to the <c>kcap</c> binary (<c>DaemonConfig.CapacitorPath</c>) — passed to
/// <see cref="ReviewLaunchBuilder.BuildAsync"/> as the review-launch MCP server command. This is
/// deliberately NOT the vendor CLI path (<c>launcher.CliPath</c>): the review agent must run
/// <c>kcap mcp review</c>, since inside the daemon the running process is <c>kcap-daemon</c> with
/// no <c>mcp review</c> subcommand of its own, and <c>claude</c>/<c>codex</c> have no such
/// subcommand at all (PR #244 review, Fix A — a post-refactor regression had passed
/// <c>launcher.CliPath</c> here instead).
/// </param>
internal sealed record RuntimeStartContext(
        string            AgentId,
        string            Vendor,
        string            SourceRepoPath,
        WorktreeInfo      Worktree,
        string?           Prompt,
        string            Model,
        string?           Effort,
        string[]?         Tools,
        bool              IsReview,
        bool              IsReviewFlow,
        ReviewLaunchInfo? Review,
        ushort            Cols,
        ushort            Rows,
        string?           ServerUrl,
        string?           DaemonBridgeUrl,
        string            CapacitorPath
    );
