using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Cursor (AI-684 Task 10): spawns
/// <c>{DaemonConfig.CursorPath} acp</c> as a child process, wraps its stdio in an
/// <see cref="AcpConnection"/> + <see cref="AcpChildProcess"/>, and drives the ACP handshake via
/// <see cref="AcpHostedAgentRuntime.StartAsync"/>. Cursor has no unattended mode yet (no permission
/// bridge until AI-686), so <see cref="SupportsUnattended"/> is <c>false</c> — the orchestrator's
/// <c>UnattendedLaunchPolicy</c> refuses a review-flow launch for this vendor.
/// </summary>
internal sealed class AcpHostedAgentRuntimeFactory(DaemonConfig config, ILoggerFactory loggerFactory) : IHostedAgentRuntimeFactory {
    public string Vendor             => "cursor";
    public bool   SupportsUnattended => false;

    public bool IsAvailable() => CliResolver.Exists(config.CursorPath);

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var psi = new ProcessStartInfo(config.CursorPath, ["acp"]) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) {
            psi.Environment["KCAP_URL"] = ctx.ServerUrl;
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{config.CursorPath} acp' (Process.Start returned null).");

        var connLogger    = loggerFactory.CreateLogger<AcpConnection>();
        var runtimeLogger = loggerFactory.CreateLogger<AcpHostedAgentRuntime>();

        var connection = new AcpConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, connLogger);
        var acpProcess = new AcpChildProcess(process);
        var runtime    = new AcpHostedAgentRuntime(connection, acpProcess, runtimeLogger);

        try {
            await runtime.StartAsync(ctx.Worktree.Path, ctx.Prompt, ct).ConfigureAwait(false);
        } catch {
            // The runtime owns both the connection and the process; dispose on a failed handshake
            // so a half-started cursor-agent child is never leaked.
            await runtime.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        return new HostedRuntimeStart(runtime, McpConfigPath: null);
    }
}
