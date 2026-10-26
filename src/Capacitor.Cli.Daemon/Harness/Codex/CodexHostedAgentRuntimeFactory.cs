using System.Diagnostics;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The single <c>codex</c> runtime factory, choosing app-server or the wrapped PTY factory per launch
/// (<see cref="Route"/>). Capability advertisement is delegated to the PTY factory: the transport
/// changes how a launch is hosted, not its containment.
/// </summary>
internal sealed class CodexHostedAgentRuntimeFactory : IHostedAgentRuntimeFactory {
    readonly TimeProvider _time;
    readonly CodexLauncher               _launcher;
    readonly IHostedAgentRuntimeFactory  _pty;
    readonly DaemonConfig                _config;
    readonly ILoggerFactory              _loggerFactory;
    readonly ILogger                     _logger;
    readonly CodexAppServerSpawnFactory  _spawnFactory;
    readonly ServerConnection?           _connection;

    /// <param name="spawnFactory">Test seam only: production passes <see langword="null"/> and the
    /// real <c>codex app-server</c> child is spawned via <see cref="Process"/>. A test can substitute
    /// an in-process fake peer to drive the app-server route without a child process.</param>
    /// <param name="connection">The server connection whose <c>RequestAcpInteractionAsync</c> forwards an
    /// interactive launch's approvals to the user (§2.3). Null in tests that never exercise interactive
    /// approvals; reviewers (approvalPolicy:never) never build an approval bridge regardless.</param>
    public CodexHostedAgentRuntimeFactory(
            CodexLauncher launcher, IHostedAgentRuntimeFactory ptyDelegate, DaemonConfig config,
            ILoggerFactory loggerFactory, TimeProvider time, CodexAppServerSpawnFactory? spawnFactory = null,
            ServerConnection? connection = null, PolicyJudgeGateway? policyJudge = null) {
        _time          = time;
        _launcher      = launcher;
        _pty           = ptyDelegate;
        _config        = config;
        _loggerFactory = loggerFactory;
        _logger        = loggerFactory.CreateLogger<CodexHostedAgentRuntimeFactory>();
        _spawnFactory  = spawnFactory
            ?? ((cli, args, seed, cwd, env, cfg, lf) => DefaultSpawnFactory(cli, args, seed, cwd, env, cfg, lf, time));
        _connection    = connection;
        _policyJudge   = policyJudge;
    }

    readonly PolicyJudgeGateway? _policyJudge;

    /// <summary>A governed interactive launch evaluates each approval before it reaches a card; a reviewer
    /// (approvals never raised) and an ungoverned launch take none.</summary>
    CodexHostedPolicySeam? PolicySeamFor(RuntimeStartContext ctx, string approval) =>
        ctx.PolicySnapshot is { IsEmpty: false } snapshot && _connection is { } connection
     && !string.Equals(approval, "never", StringComparison.Ordinal)
            ? new CodexHostedPolicySeam(snapshot, _policyJudge, evt => _ = connection.AppendAgentRunEventAsync(ctx.AgentId, evt),
                new PermissionRefusalLedger(), ctx.PersistenceProbe ?? (() => null), ctx.AgentId, _time,
                _loggerFactory.CreateLogger<CodexHostedPolicySeam>(), ctx.Worktree.Path)
            : null;

    public string Vendor => "codex";

    // Capability advertisement is transport-independent — delegate every member to the PTY factory.
    public string           CliPath                                    => _pty.CliPath;
    public bool             IsAvailable()                              => _pty.IsAvailable();
    public bool             SupportsUnattended                          => _pty.SupportsUnattended;
    public bool             SupportsPrReview                            => _pty.SupportsPrReview;
    public UnattendedSupport DescribeUnattendedSupport()               => _pty.DescribeUnattendedSupport();
    public bool             SupportsBorrowedReviewFlow                  => _pty.SupportsBorrowedReviewFlow;
    public bool             BorrowedReviewRequiresIndependentSnapshot   => _pty.BorrowedReviewRequiresIndependentSnapshot;
    public string?          BorrowedReviewContainment                   => _pty.BorrowedReviewContainment;
    public bool             ReviewFlowRedirectsHome                     => _pty.ReviewFlowRedirectsHome;
    public IReviewerModelResolver? ReviewerModelResolver               => _pty.ReviewerModelResolver;
    public bool             SupportsModelSelection                      => _pty.SupportsModelSelection;

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) {
        var dir = CodexModelCatalogProbe.DirectoryFor(_config.Store.StateDirectory(_config.Name));

        return CodexModelCatalogProbe.RunAsync(
            () => {
                Directory.CreateDirectory(dir);
                return _spawnFactory(_pty.CliPath, [], null, dir, new Dictionary<string, string>(), _config, _loggerFactory);
            },
            string.IsNullOrEmpty(_config.Version) ? "0.0.0" : _config.Version, _time, _logger, ct);
    }

    /// <summary>Codex rewrites its cached catalog when it refetches it, and its auth file when the
    /// account changes; either can change the models this account may launch.</summary>
    public IReadOnlyList<string> CatalogFingerprintPaths => [_launcher.Paths.ModelsCache, _launcher.Paths.AuthJson];

    // A default-kind launch is the only Codex shape that accepts a follow-up while running, so it is
    // the only one whose fetch happens alongside a live, write-contained process: the store keeps
    // that write outside every cwd it could steer. Protected kinds fetch before the process exists.
    public AttachmentPlacement AttachmentPlacementFor(LaunchKind kind) =>
        kind == LaunchKind.Default ? AttachmentPlacement.DaemonStore : AttachmentPlacement.Worktree;

    /// <summary>
    /// A pinned transport wins: the ledger's for a resume, else the one the server will validate the
    /// registration against. Unpinned, a PR review stays on PTY and anything else follows the resolution
    /// the server last acknowledged, since that is the advertisement it dispatched against. A pinned
    /// app-server launch on a Codex below the floor is refused, never moved to PTY, which the server
    /// would refuse at registration.
    /// </summary>
    internal CodexRoute Route(RuntimeStartContext ctx) {
        var pinned = Recognised(ctx.RequestedTransport) ?? Recognised(ctx.ExpectedRuntimeTransport);
        if (pinned == CodexTransportDecision.Pty) return new(AppServer: false, Refusal: null);
        if (pinned == CodexTransportDecision.AppServer) {
            var version = _config.CodexResolution.CliVersion ?? DaemonRunner.ProbeCliVersionForLaunch(_pty.CliPath);
            return CodexTransportDecision.MeetsFloor(version)
                ? new(AppServer: true, Refusal: null)
                : new(AppServer: false, Refusal:
                    $"codex: this launch must run on app-server, which needs Codex {CodexTransportDecision.VersionFloor} or later; " +
                    $"the installed Codex is {version ?? "of an unreadable version"}.");
        }

        if (ctx.IsReview && !ctx.IsReviewFlow) return new(AppServer: false, Refusal: null);

        return new(AppServer: _config.AcknowledgedCodexResolution.Active, Refusal: null);
    }

    static string? Recognised(string? transport) =>
        transport is CodexTransportDecision.AppServer or CodexTransportDecision.Pty ? transport : null;

    public bool SupportsRestartResume(string? runtimeTransport) => true;

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var route = Route(ctx);
        if (route.Refusal is { } refusal)
            return Task.FromException<HostedRuntimeStart>(new InvalidOperationException(refusal));

        // A parked reviewer's transcript is envelope-sourced; a PTY resume would fork it.
        if (ctx.ResumeSessionId is not null && ctx.IsReviewFlow && !route.AppServer)
            return Task.FromException<HostedRuntimeStart>(
                new InvalidOperationException("codex: a reviewer resume requires the app-server route; refusing to start a PTY session."));

        return route.AppServer ? StartAppServerAsync(ctx, ct) : _pty.StartAsync(ctx, ct);
    }

    async Task<HostedRuntimeStart> StartAppServerAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var launcherCtx = BuildLauncherContext(ctx);

        // Fail-closed hooks preflight + TrustWorktree config, exactly as the PTY path — a missing
        // critical hook throws CodexHooksNotInstalledException for the orchestrator's cleanup path.
        var owned  = launcherCtx.Work == Core.LocalIpc.WorkLocation.OwnedWorktree;
        var before = owned ? WorktreeManager.StampDaemonPaths(launcherCtx.Worktree.Path) : null;
        try { _launcher.Prepare(launcherCtx); }
        finally { if (owned) WorktreeManager.RecordDaemonBaseline(launcherCtx.Worktree.Path, before); }

        var (sandbox, approval) = CodexPosturePolicy.Resolve(ctx.Work, ctx.IsReviewFlow, ctx.CodexPosture);
        var appServerArgs = _launcher.BuildAppServerLaunchArgs(launcherCtx);

        // The app-server path is envelope-sourced: the daemon emits the transcript and defers the first
        // turn behind a source claim, guard-1 stands the rollout watcher down (the marker), and guard-2
        // backstops server-side. ONE decision drives all four (marker, emission, deferral, forwarder) so
        // they can never desync.
        const bool envelopeSourced = true;
        var env = BuildEnv(ctx, envelopeSourced);

        var launch = new CodexAppServerLaunch(
            Cwd:           ctx.Worktree.Path,
            Model:         ctx.Model,
            Effort:        ctx.Effort,
            InitialPrompt: ctx.Prompt,
            Sandbox:       sandbox,
            Approval:      approval,
            WritableRoots: [ctx.Worktree.Path],
            ClientVersion: string.IsNullOrEmpty(_config.Version) ? "0.0.0" : _config.Version,
            // Non-null only for a parked reviewer relaunch: the runtime reopens this thread via
            // thread/resume instead of thread/start (and the orchestrator suppresses the second
            // SessionStarted, since the resumed thread keeps its canonical id).
            ResumeSessionId: ctx.ResumeSessionId);

        CodexAppServerSpawn spawn = (seed, spawnCt) =>
            _spawnFactory(_launcher.CliPath, appServerArgs, seed, ctx.Worktree.Path, env, _config, _loggerFactory);

        ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);

        var runtime = new CodexAppServerHostedAgentRuntime(
            spawn, launch, ctx.ActivityClock,
            _loggerFactory.CreateLogger<CodexAppServerHostedAgentRuntime>(), _time,
            emitEnvelopeTranscript: envelopeSourced,
            deferFirstTurn: envelopeSourced,
            agentId: ctx.AgentId,
            requestInteraction: _connection is { } c ? c.RequestAcpInteractionAsync : null,
            approvalTimeout: TimeSpan.FromSeconds(Math.Max(1, _config.CodexAppServerApprovalTimeoutSeconds)),
            journal: ctx.Journal,
            policy: PolicySeamFor(ctx, approval));

        // StartAsync may spawn a child before it throws (a failed hooks/list, thread/start, or initial
        // turn on the fail-closed paths). The orchestrator never receives a runtime it did not get a
        // HostedRuntimeStart for, so dispose it here to avoid leaking a live Codex child.
        try {
            await runtime.StartAsync(ct).ConfigureAwait(false);
        } catch {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // Transcript: runtime → the orchestrator drains the runtime's envelopes through an
        // AcpTranscriptForwarder and, because RequiresSourceClaimBeforeFirstTurn is now set, runs the
        // source-claim → BeginFirstTurn → confirm sequence before the first turn is dispatched.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    static LauncherContext BuildLauncherContext(RuntimeStartContext ctx) => new(
        AgentId:        ctx.AgentId,
        SourceRepoPath: ctx.SourceRepoPath,
        Worktree:       ctx.Worktree,
        Prompt:         ctx.Prompt,
        Model:          ctx.Model,
        Effort:         ctx.Effort,
        Tools:          ctx.Tools,
        IsReview:       ctx.IsReview,
        IsReviewFlow:   ctx.IsReviewFlow,
        Review:         ctx.Review,
        // App-server review flows carry no PR-review MCP launch (ctx.IsReview is false); the
        // flow-result + allowlist servers are spliced by BuildAppServerLaunchArgs from IsReviewFlow.
        ReviewLaunch:   null) {
        McpAllowlist = ctx.McpAllowlist,
        Work         = ctx.Work,
        CodexPosture = ctx.CodexPosture,
    };

    internal const string HostedAppServerMarkerEnv = "KCAP_HOSTED_APPSERVER";

    internal static Dictionary<string, string> BuildEnv(RuntimeStartContext ctx, bool emitEnvelopeTranscript) {
        var env = new Dictionary<string, string> {
            [HostedAgent.RenderedVar] = HostedAgent.Rendered,
            [HostedAgent.AgentIdVar]  = ctx.AgentId,
        };
        HostedLaunchMarkers.ApplyOverlay(env, ctx);
        if (!string.IsNullOrEmpty(ctx.DaemonId))        env["KCAP_DAEMON_ID"]         = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch))     env["KCAP_DAEMON_EPOCH"]      = ctx.DaemonEpoch;
        if (!string.IsNullOrEmpty(ctx.ServerUrl))       env[ProfileOverrides.UrlVar]  = ctx.ServerUrl;
        if (!string.IsNullOrEmpty(ctx.DaemonBridgeUrl)) env[HostedAgent.BridgeUrlVar] = ctx.DaemonBridgeUrl;
        // guard-1: only an envelope-sourced session carries this marker; the codex hook + watcher read it
        // and stand down so the rollout is not double-ingested alongside the envelopes.
        if (emitEnvelopeTranscript) env[HostedAppServerMarkerEnv] = "1";
        return env;
    }

    // Materialize the child's environment over the daemon's own, where nothing inherited may speak for
    // this launch: an inherited envelope marker would make a non-emitting child's hook watcher stand
    // down and lose ingestion, and an inherited session or lookup agent names another session.
    internal static void ApplyChildEnv(IDictionary<string, string?> childEnv, IReadOnlyDictionary<string, string> overlay) {
        childEnv.Remove(HostedAppServerMarkerEnv);
        childEnv.Remove(HostedAgent.FlowRoleVar);
        childEnv.Remove(HostedAgent.DaemonAgentIdVar);
        InheritedHarnessSessions.Remove(childEnv);
        foreach (var (k, v) in overlay) childEnv[k] = v;
    }

    /// <summary>The real spawn: <c>codex app-server</c> as a child process, its stdio wrapped by the
    /// shared <see cref="AcpChildProcess"/> (stderr drain + terminate) and the JSON-RPC transport.</summary>
    static Task<(CodexAppServerConnection, IAcpProcess)> DefaultSpawnFactory(
            string cliPath, IReadOnlyList<string> appServerArgs, string? hookStateSeed, string cwd,
            IReadOnlyDictionary<string, string> env, DaemonConfig config, ILoggerFactory loggerFactory,
            TimeProvider time) {
        var argv = new List<string> { "app-server" };
        argv.AddRange(appServerArgs);
        if (!string.IsNullOrEmpty(hookStateSeed)) {
            argv.Add("-c");
            argv.Add(hookStateSeed);
        }

        var psi = new ProcessStartInfo(cliPath, argv) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = cwd,
        };
        ApplyChildEnv(psi.Environment, env);
        WindowsLaunchCommand.Apply(psi, BinaryProbe.FromEnvironment());

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{cliPath} {string.Join(' ', argv)}'.");
        var child = new AcpChildProcess(
            process, loggerFactory.CreateLogger<AcpChildProcess>(), time, config.DebugFrames, "codex");
        var conn  = new CodexAppServerConnection(
            process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            loggerFactory.CreateLogger<CodexAppServerConnection>(), config.DebugFrames);

        return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((conn, child));
    }
}

/// <summary>Test seam for how a <c>codex app-server</c> child (and its transport) is produced —
/// production spawns a real process; a test substitutes an in-process fake peer.</summary>
internal delegate Task<(CodexAppServerConnection Connection, IAcpProcess Process)> CodexAppServerSpawnFactory(
    string cliPath, IReadOnlyList<string> appServerArgs, string? hookStateSeed, string cwd,
    IReadOnlyDictionary<string, string> env, DaemonConfig config, ILoggerFactory loggerFactory);
