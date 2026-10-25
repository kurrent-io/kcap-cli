using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Thrown by <see cref="CodexLauncher"/>'s Prepare preflight when neither the launch account's
/// <c>hooks.json</c> nor the worktree's <c>.codex/hooks.json</c> wires SessionStart / Stop /
/// PermissionRequest to kcap. The orchestrator reports the message as <see cref="LaunchFailed"/>.
/// </summary>
internal sealed class CodexHooksNotInstalledException(string message) : Exception(message);
