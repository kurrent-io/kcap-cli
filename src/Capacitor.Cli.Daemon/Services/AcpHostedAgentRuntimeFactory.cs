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
///
/// <b>Spec-review Finding 4 (AI-686):</b> gained a <see cref="ServerConnection"/> constructor
/// dependency so every runtime this factory produces has the real permission/elicitation bridge
/// wired — <see cref="StartAsync"/> passes <c>ctx.AgentId</c> and
/// <see cref="ServerConnection.RequestAcpInteractionAsync"/> into <see cref="AcpHostedAgentRuntime"/>'s
/// optional parameters instead of leaving them at their <c>""</c>/<see langword="null"/> defaults.
///
/// <b>Round-4 Finding 3:</b> process-spawning + stream construction is extracted into
/// <paramref name="connectionSource"/> (defaulting to <see cref="StartRealProcess"/>) purely so
/// <see cref="AcpHostedAgentRuntimeFactoryTests"/> can construct THIS class for real and drive its
/// REAL <see cref="StartAsync"/> against an in-memory <c>FakeAcpAgent</c> peer instead of a real
/// <c>cursor-agent acp</c> child process (unavailable and non-portable in CI) — the seam changes
/// nothing about production behavior, since the default IS the real `Process.Start`-backed path.
/// </summary>
internal sealed partial class AcpHostedAgentRuntimeFactory(
        DaemonConfig                                                                   config,
        ILoggerFactory                                                                 loggerFactory,
        ServerConnection                                                               connection,
        Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)>? connectionSource = null
    ) : IHostedAgentRuntimeFactory {
    readonly Func<RuntimeStartContext, (Stream Input, Stream Output, IAcpProcess Process)> _connectionSource =
        connectionSource ?? (ctx => StartRealProcess(config, ctx, loggerFactory));

    readonly ILogger _logger = loggerFactory.CreateLogger<AcpHostedAgentRuntimeFactory>();

    public string Vendor             => "cursor";
    public bool   SupportsUnattended => false;

    public bool IsAvailable() => CliResolver.Exists(config.CursorPath);

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        LogLaunching(ctx.AgentId, Vendor, ctx.Worktree.Path);
        AcpMetrics.Launches.Add(1);

        var runtimeLogger = loggerFactory.CreateLogger<AcpHostedAgentRuntime>();
        var connLogger    = loggerFactory.CreateLogger<AcpConnection>();

        var (input, output, acpProcess) = _connectionSource(ctx);
        var acpConnection = new AcpConnection(input, output, connLogger, config.DebugFrames);

        // Spec-review Finding 4: real production wiring — every Cursor launch now gets the
        // permission/elicitation bridge, not AI-684's default MethodNotFound/decline.
        var runtime = new AcpHostedAgentRuntime(
            acpConnection,
            acpProcess,
            runtimeLogger,
            agentId: ctx.AgentId,
            requestInteraction: connection.RequestAcpInteractionAsync,
            debugFrames: config.DebugFrames
        );

        try {
            await runtime.StartAsync(ctx.Worktree.Path, ctx.Prompt, ct, ResolveRequestedModel(config, ctx)).ConfigureAwait(false);
        } catch {
            // The runtime owns both the connection and the process; dispose on a failed handshake
            // so a half-started cursor-agent child is never leaked.
            await runtime.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        // The runtime IS the transcript source (it implements
        // IAcpTranscriptSource directly) — hand it back on HostedRuntimeStart so the orchestrator can
        // bind + forward without downcasting Runtime.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>
    /// Merges the per-launch model override with the daemon-wide default —
    /// <paramref name="ctx"/>'s own <c>Model</c> takes precedence when the launch specifies one,
    /// else falls back to <paramref name="config"/>'s <c>CursorModel</c>. Mirrors the existing
    /// <c>"default"</c>-sentinel convention <c>CodexLauncher.AddModelArg</c> already uses for "no
    /// override requested" (the UI dispatches the literal string <c>"default"</c>, not an empty
    /// string, when the user hasn't picked a model). The merged value is still a bare family prefix
    /// or an exact <c>modelId</c> — final resolution against the session's <c>availableModels</c>
    /// happens in <see cref="AcpHostedAgentRuntime"/> via
    /// <see cref="Capacitor.Cli.Core.Acp.AcpModelResolver"/>.
    /// </summary>
    static string? ResolveRequestedModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.CursorModel;

    /// <summary>
    /// The REAL, production process-spawning path — unchanged in behavior from the pre-round-4
    /// shape of this factory, just extracted into a named method so <paramref name="connectionSource"/>
    /// can default to it. Spawns <c>{config.CursorPath} acp</c> and returns its stdio streams plus
    /// an <see cref="AcpChildProcess"/> lifecycle wrapper.
    /// </summary>
    static (Stream Input, Stream Output, IAcpProcess Process) StartRealProcess(DaemonConfig config, RuntimeStartContext ctx, ILoggerFactory loggerFactory) {
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

        var processLogger = loggerFactory.CreateLogger<AcpChildProcess>();
        var acpProcess    = new AcpChildProcess(process, processLogger, config.DebugFrames);

        return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, acpProcess);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP hosted agent launch: agentId={AgentId} vendor={Vendor} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string vendor, string cwd);
}
