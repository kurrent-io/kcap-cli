using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Commands;

namespace Kapacitor.Cli.Daemon.Services;

/// <summary>
/// Vendor-specific launch strategy. <see cref="AgentOrchestrator"/> handles
/// lifecycle concerns (PTY spawn, status, heartbeat, cleanup); each impl owns
/// the vendor-specific bits: CLI binary path, args, settings overlay, pre-trust,
/// MCP config, and per-vendor cleanup.
/// </summary>
internal interface IHostedAgentLauncher {
    /// <summary>Vendor token this launcher handles ("claude" or "codex").</summary>
    string Vendor { get; }

    /// <summary>Absolute path or bare command for the CLI. Pulled from DaemonConfig.</summary>
    string CliPath { get; }

    /// <summary>
    /// Reports whether <see cref="CliPath"/> resolves to an executable that
    /// looks installed. Used at daemon startup (AI-652) to build the list of
    /// supported vendors advertised over <c>DaemonConnect</c>, so the launch
    /// dialog only offers vendors the daemon can actually spawn.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Per-vendor preparation BEFORE the PTY is spawned. Implementations:
    ///   • Overlay vendor-specific settings dir from source repo into worktree
    ///   • Pre-trust the worktree path in the vendor's config file
    ///   • Write any vendor-specific config (MCP, etc.)
    ///   • Merge dialog-selected tools into vendor-specific permission shape
    ///   • Run fail-fast preflight checks (e.g. required CLI hooks installed)
    /// Two failure modes are supported and the orchestrator distinguishes them:
    ///   • Filesystem / parse errors are swallowed inside the launcher with a
    ///     warning log so a settings-overlay glitch never blocks launch.
    ///   • Typed preflight exceptions (e.g. CodexHooksNotInstalledException)
    ///     propagate out and the orchestrator converts them into LaunchFailed
    ///     with the exception's user-facing message. Use these sparingly.
    /// </summary>
    void Prepare(LauncherContext ctx);

    /// <summary>Build the argv array passed to the CLI.</summary>
    LaunchArgs BuildArgs(LauncherContext ctx);

    /// <summary>Per-vendor cleanup AFTER the agent exits / is stopped.</summary>
    void Cleanup(AgentInstance agent);
}

internal sealed record LauncherContext(
        string                            AgentId,
        string                            SourceRepoPath,
        WorktreeInfo                      Worktree,
        string?                           Prompt,
        string                            Model,
        string?                           Effort,
        string[]?                         Tools,
        bool                              IsReview,
        ReviewLaunchInfo?                 Review,
        ReviewLaunchBuilder.ReviewLaunch? ReviewLaunch
    );

internal readonly record struct LaunchArgs(string[] Args, string? McpConfigPath);
