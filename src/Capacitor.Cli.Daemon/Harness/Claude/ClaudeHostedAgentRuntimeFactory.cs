using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// Hosts Claude on one of two transports. Under <c>stream-json</c> an interactive launch runs
/// <c>claude -p</c> and gets its prompt as its first stdin message, so the prompt never reaches the
/// command line; every other launch, and every launch under <c>pty</c>, goes to the PTY factory.
/// </summary>
internal sealed partial class ClaudeHostedAgentRuntimeFactory : IHostedAgentRuntimeFactory {
    public const string Pty        = "pty";
    public const string StreamJson = "stream-json";

    static readonly string[] StreamJsonArgs = [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        "--permission-prompt-tool", "stdio"
    ];

    readonly IHostedAgentLauncher                             _launcher;
    readonly IHostedAgentRuntimeFactory                       _pty;
    readonly DaemonConfig                                     _config;
    readonly LocalPermissionBridge                            _bridge;
    readonly ClaudePaths                                      _paths;
    readonly ILoggerFactory                                   _loggerFactory;
    readonly TimeProvider                                     _time;
    readonly ILogger                                          _logger;
    readonly Func<ProcessStartInfo, IJsonLineProcess>         _processSource;

    /// <param name="processSource">Replaces the real spawn; production passes null.</param>
    public ClaudeHostedAgentRuntimeFactory(
            IHostedAgentLauncher                      launcher,
            IHostedAgentRuntimeFactory                ptyDelegate,
            DaemonConfig                              config,
            LocalPermissionBridge                     bridge,
            ClaudePaths                               paths,
            ILoggerFactory                            loggerFactory,
            TimeProvider                              time,
            Func<ProcessStartInfo, IJsonLineProcess>? processSource = null) {
        _launcher      = launcher;
        _pty           = ptyDelegate;
        _config        = config;
        _bridge        = bridge;
        _paths         = paths;
        _loggerFactory = loggerFactory;
        _time          = time;
        _logger        = loggerFactory.CreateLogger<ClaudeHostedAgentRuntimeFactory>();
        _processSource = processSource ?? (psi => new JsonLineProcess(
            WindowsLaunchCommand.Apply(psi, config.Binaries), loggerFactory.CreateLogger<JsonLineProcess>(), time));
    }

    public string Vendor => "claude";

    public string                  CliPath                                  => _pty.CliPath;
    public bool                    IsAvailable()                            => _pty.IsAvailable();
    public bool                    SupportsUnattended                       => _pty.SupportsUnattended;
    public UnattendedSupport       DescribeUnattendedSupport()              => _pty.DescribeUnattendedSupport();
    public bool                    SupportsPrReview                         => _pty.SupportsPrReview;
    public bool                    SupportsBorrowedReviewFlow               => _pty.SupportsBorrowedReviewFlow;
    public string?                 BorrowedReviewContainment                => _pty.BorrowedReviewContainment;
    public bool                    BorrowedReviewRequiresIndependentSnapshot => _pty.BorrowedReviewRequiresIndependentSnapshot;
    public bool                    ReviewFlowRedirectsHome                  => _pty.ReviewFlowRedirectsHome;
    public IReviewerModelResolver? ReviewerModelResolver                    => _pty.ReviewerModelResolver;
    public bool                    SupportsModelSelection                   => _pty.SupportsModelSelection;
    public bool                    SupportsRestartResume(string? runtimeTransport) => _pty.SupportsRestartResume(runtimeTransport);

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) => _pty.ProbeModelsAsync(ct);
    public IReadOnlyList<string>                   CatalogFingerprintPaths               => _pty.CatalogFingerprintPaths;
    public AttachmentPlacement AttachmentPlacementFor(LaunchKind kind) => _pty.AttachmentPlacementFor(kind);

    /// <summary>Reviews keep the PTY, and so does a private agent, which has no bridge to answer its
    /// prompts. Without a bridge URL the hook would raise its own card beside the runtime's.</summary>
    internal bool UsesStreamJson(RuntimeStartContext ctx) =>
        _config.ClaudeTransport == StreamJson
     && !ctx.IsReview && !ctx.IsReviewFlow && !ctx.IsPrivate
     && ctx.Work == WorkLocation.OwnedWorktree
     && !string.IsNullOrEmpty(ctx.DaemonBridgeUrl);

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
        UsesStreamJson(ctx) ? StartStreamJsonAsync(ctx, ct) : _pty.StartAsync(ctx, ct);

    async Task<HostedRuntimeStart> StartStreamJsonAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var launcherCtx = await PtyHostedAgentRuntimeFactory.PrepareAsync(_launcher, ctx, ex => LogPrepareSoftFailure(ex, ctx.AgentId));
        var launchArgs  = _launcher.BuildArgs(launcherCtx with { Prompt = null });

        LogLaunching(ctx.AgentId, ctx.Worktree.Path);

        var process = _processSource(BuildPsi(_launcher.CliPath, [.. launchArgs.Args, .. StreamJsonArgs], ctx));
        var runtime = new ClaudeStreamJsonHostedAgentRuntime(
            process,
            (request, token) => _bridge.DecideAsync(request, Vendor, token),
            sessionId => Path.Combine(_paths.ProjectDir(ctx.Worktree.Path), sessionId + ".jsonl"),
            ctx.AgentId,
            ctx.Worktree.Path,
            _loggerFactory.CreateLogger<ClaudeStreamJsonHostedAgentRuntime>(),
            _time);

        try {
            ct.ThrowIfCancellationRequested();
            await runtime.SendInitializeAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ctx.Prompt)) await runtime.SendUserInputAsync(ctx.Prompt).ConfigureAwait(false);
        } catch (Exception ex) {
            var stderr = process.Diagnostics;
            await runtime.DisposeAsync().ConfigureAwait(false);

            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;

            // stderr goes to the daemon log only: this exception is forwarded to the server.
            if (stderr is { Length: > 0 }) {
                LogLaunchStderr(stderr);
                throw new InvalidOperationException($"{ex.Message} (stderr captured in daemon log)", ex);
            }

            throw;
        }

        return new HostedRuntimeStart(runtime, launchArgs.McpConfigPath);
    }

    /// <summary>The child gets what a PTY child gets: the daemon's environment without its session,
    /// supervision and boot-local variables, plus the hosted agent's own.</summary>
    internal static ProcessStartInfo BuildPsi(string cliPath, IReadOnlyList<string> args, RuntimeStartContext ctx) {
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var psi = new ProcessStartInfo(cliPath) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = noBomUtf8,
            StandardOutputEncoding = noBomUtf8,
            StandardErrorEncoding  = noBomUtf8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        foreach (var key in PtyEnvScrub.ClaudeSessionVars) psi.Environment.Remove(key);
        foreach (var key in PtyEnvScrub.HostedAgentVars) psi.Environment.Remove(key);
        foreach (var key in PtyEnvScrub.DaemonSupervisionVars) psi.Environment.Remove(key);
        foreach (var key in DaemonRunner.BootCarriers.All) psi.Environment.Remove(key);
        foreach (var (key, value) in PtyHostedAgentRuntimeFactory.HostedEnv(ctx)) psi.Environment[key] = value;

        return psi;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claude stream-json launch: agentId={AgentId} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string cwd);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Launcher Prepare soft-failure for agent {AgentId} (continuing)")]
    partial void LogPrepareSoftFailure(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude stream-json launch failed; captured stderr follows (not sent to server): {Diagnostics}")]
    partial void LogLaunchStderr(string diagnostics);
}
