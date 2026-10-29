using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Muse Code: one <c>muse serve</c> child per agent,
/// driven over MSP by <see cref="MuseMspHostedAgentRuntime"/>, as an interactive agent or an
/// unattended review-flow reviewer.
///
/// <para>The session is recorded from Muse's own log by <c>kcap hook --muse</c>, which finds the
/// hosting agent, and the daemon's config root and server, through a marker written here under the
/// session id this factory mints. When the daemon host has no kcap hooks in Muse's settings, the
/// factory runs the hook itself.</para>
///
/// <para>One refusal ladder (<see cref="ReviewerRefusal"/>) answers both advertisement and launch, so
/// the daemon never offers a reviewer it would refuse to start.</para>
/// </summary>
internal sealed partial class MuseHostedAgentRuntimeFactory : IHostedAgentRuntimeFactory {
    static readonly TimeSpan MarkerMaxAge = TimeSpan.FromHours(24);

    const string SandboxUnavailable =
        "muse_reviewer_sandbox_unavailable: bubblewrap cannot create namespaces on this host (bwrap --unshare-all true "
      + "failed); run the daemon where user namespaces are permitted.";

    readonly DaemonConfig            _config;
    readonly ILoggerFactory          _loggerFactory;
    readonly TimeProvider            _time;
    readonly ServerConnection        _connection;
    readonly MusePaths               _paths;
    readonly PermissionPromptBroker? _permissionBroker;
    readonly PermissionDecisionLog?  _permissionDecisionLog;
    readonly MuseSandboxProbe        _sandbox;
    readonly TimeSpan?               _readyDeadline;
    readonly bool                    _posixHost;
    readonly ILogger                 _logger;

    readonly Func<ProcessStartInfo, CancellationToken, Task<IMuseMspProcess>> _processSource;
    readonly Func<string, bool>                                               _binaryExists;
    readonly Func<string, string?>                                            _resolveVersion;
    readonly Func<string, bool>                                               _hooksInstalled;
    readonly Func<string, JsonObject, string, string, string?, string, string, ILogger, CancellationToken, Task> _hookRelay;

    /// <param name="processSource">Replaces the real spawn; production passes null.</param>
    /// <param name="binaryExists">Replaces the binary lookup; production passes null.</param>
    /// <param name="resolveVersion">Replaces the installed-version probe; production passes null.</param>
    /// <param name="posixHost">Replaces the ambient OS; production passes null.</param>
    /// <param name="hooksInstalled">Whether kcap's hooks are in Muse's settings; production passes null.</param>
    /// <param name="hookRelay">Replaces <see cref="MuseHookRelay.RelayAsync"/>; production passes null.</param>
    public MuseHostedAgentRuntimeFactory(
            DaemonConfig                                                    config,
            ILoggerFactory                                                  loggerFactory,
            TimeProvider                                                    time,
            ServerConnection                                                connection,
            MusePaths                                                       paths,
            PermissionPromptBroker?                                         permissionBroker      = null,
            PermissionDecisionLog?                                          permissionDecisionLog = null,
            Func<ProcessStartInfo, CancellationToken, Task<IMuseMspProcess>>? processSource       = null,
            Func<string, bool>?                                             binaryExists          = null,
            Func<string, string?>?                                          resolveVersion        = null,
            bool?                                                           posixHost             = null,
            MuseSandboxProbe?                                               sandbox               = null,
            Func<string, bool>?                                             hooksInstalled        = null,
            Func<string, JsonObject, string, string, string?, string, string, ILogger, CancellationToken, Task>? hookRelay = null,
            TimeSpan?                                                       readyDeadline         = null) {
        _config                = config;
        _loggerFactory         = loggerFactory;
        _time                  = time;
        _connection            = connection;
        _paths                 = paths;
        _permissionBroker      = permissionBroker;
        _permissionDecisionLog = permissionDecisionLog;
        _sandbox               = sandbox ?? new MuseSandboxProbe();
        _readyDeadline         = readyDeadline;
        _posixHost             = posixHost ?? !OperatingSystem.IsWindows();
        _logger                = loggerFactory.CreateLogger<MuseHostedAgentRuntimeFactory>();

        _processSource  = processSource ?? ((psi, _) => Task.FromResult<IMuseMspProcess>(
            new MuseMspProcess(WindowsLaunchCommand.Apply(psi, config.Binaries), loggerFactory.CreateLogger<MuseMspProcess>(), time)));
        _binaryExists   = binaryExists   ?? (path => config.Binaries.Finds(path));
        _resolveVersion = resolveVersion ?? (path => new VendorVersionResolver(config.Binaries).Resolve(path));
        _hooksInstalled = hooksInstalled ?? MuseHooksInstaller.IsInstalled;
        _hookRelay      = hookRelay      ?? MuseHookRelay.RelayAsync;

        SweepStaleMarkers();
    }

    public string Vendor => DaemonRunner.MuseVendor;

    public string CliPath => _config.MusePath;

    public bool IsAvailable() => _binaryExists(_config.MusePath);

    public bool SupportsUnattended => DescribeUnattendedSupport().Supported;

    /// <summary>Muse's sandbox confines writes to the workspace but not reads, so a borrowed review
    /// runs only in a daemon-owned snapshot.</summary>
    public bool SupportsBorrowedReviewFlow => SupportsUnattended;

    public string? BorrowedReviewContainment => SupportsBorrowedReviewFlow ? "independent-snapshot" : null;

    public bool BorrowedReviewRequiresIndependentSnapshot => true;

    public bool SupportsPrReview => false;

    public bool SupportsModelSelection => true;

    public bool ReviewFlowRedirectsHome => false;

    public bool SupportsRestartResume(string? runtimeTransport) => true;

    public UnattendedSupport DescribeUnattendedSupport() =>
        ReviewerRefusal() is { } reason ? new(false, reason) : new(true, null);

    /// <summary>The models Meta currently serves, from the catalog Muse caches on each run. The server's
    /// own feed keeps retired ids, which Muse rejects on every turn, so a present cache replaces it.</summary>
    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) =>
        Task.FromResult(MuseModelCatalog.Read(_paths.ModelCatalogDir));

    public IReadOnlyList<string> CatalogFingerprintPaths =>
        [_paths.SettingsJson, Path.Combine(_paths.ConfigRoot, "auth.json"), .. MuseModelCatalog.Files(_paths.ModelCatalogDir)];

    internal static ReviewerVersionStore VersionStoreFor(DaemonConfig config) =>
        new(config.Store.StateDirectory(config.Name), DaemonRunner.MuseVendor);

    /// <summary>Null when this daemon may launch a Muse reviewer, else the coded refusal. Consent and
    /// platform are decided before the binary is probed.</summary>
    internal string? ReviewerRefusal() {
        var beforeProbe = MuseReviewerCapability.Decide(
            _posixHost, _config.MuseUnattendedReviewerEnabled, installedVersion: null, minimumVersion: null);

        if (beforeProbe != MuseReviewerDecision.VersionUnresolved)
            return MuseReviewerCapability.DenialReason(beforeProbe, null, null, _config.MusePath);

        if (!IsAvailable())
            return $"muse_reviewer_binary_missing: '{_config.MusePath}' does not resolve to an executable. Install "
                 + $"muse, or set {HarnessId.Muse.PathEnvVar} to its location.";

        var installed = _resolveVersion(_config.MusePath);
        var minimum   = VersionStoreFor(_config).Affirmed;
        var decision  = MuseReviewerCapability.Decide(_posixHost, _config.MuseUnattendedReviewerEnabled, installed, minimum);

        if (decision != MuseReviewerDecision.Allowed)
            return MuseReviewerCapability.DenialReason(decision, installed, minimum, _config.MusePath);

        return _sandbox.Available ? null : SandboxUnavailable;
    }

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        if (ctx.IsReview)
            throw new InvalidOperationException(
                "muse_pr_review_unsupported: this runtime hosts interactive agents and review-flow reviewers only. "
              + "A PR review needs the `kcap mcp review` tool surface, which only the PTY launchers build.");

        if (ctx.IsReviewFlow && ReviewerRefusal() is { } refusal)
            throw new InvalidOperationException(refusal);

        if (ctx.Work != WorkLocation.OwnedWorktree && !ctx.IsBorrowedSnapshot)
            throw new InvalidOperationException(
                "muse_requires_owned_worktree: Muse's sandbox does not confine reads, so this runtime runs only "
              + "in a daemon-owned worktree or snapshot.");

        if (ctx.IsBorrowedSnapshot && (!ctx.IsReviewFlow || ctx.Worktree.SnapshotRoot is null))
            throw new InvalidOperationException("borrowed_snapshot_containment_mismatch");

        var plan = BuildPlan(ref ctx, _config);

        // An installed hook resolves its own root and profile, which need not be the daemon's.
        MuseHostedSessionMarker.Write(_config.Home, new(
            plan.MintedSessionId, ctx.AgentId, ctx.DaemonId, ctx.DaemonEpoch, _time.GetUtcNow(),
            NonDefaultConfigDir(_config),
            string.IsNullOrEmpty(ctx.ServerUrl) ? null : ctx.ServerUrl,
            _config.Profiles.Resolution.ProfileName is { Length: > 0 } profile ? profile : null,
            (ctx.IsReviewFlow ? HostedSessionKind.FlowParticipant : HostedSessionKind.HostedAgent).ToWire(),
            ctx.IsReviewFlow ? ctx.FlowRole : null));

        var relay        = !_hooksInstalled(_paths.SettingsJson);
        var relayedStart = false;

        IMuseMspProcess?           process = null;
        MuseMspHostedAgentRuntime? runtime = null;

        try {
            LogLaunching(ctx.AgentId, ctx.Worktree.Path, plan.IsReviewer);
            process = await _processSource(BuildPsi(_config, ctx, plan.IsReviewer), ct).ConfigureAwait(false);

            var launch = ctx;
            runtime = new MuseMspHostedAgentRuntime(
                process,
                _loggerFactory.CreateLogger<MuseMspHostedAgentRuntime>(),
                ctx.AgentId,
                ctx.Worktree.Path,
                MuseLaunchArgs.ResolveModel(_config, ctx),
                _time,
                RequestInteraction,
                plan,
                _config.Version ?? "0.0.0",
                _readyDeadline,
                onDisposed: () => OnRuntimeDisposedAsync(runtime, launch, plan, relayedStart));
            runtime.ActivityClock = ctx.ActivityClock;

            await runtime.WaitForSessionReadyAsync(ct).ConfigureAwait(false);

            if (runtime.Handshake?.SandboxAvailability is var sandbox && sandbox != "available") {
                if (plan.IsReviewer)
                    throw new InvalidOperationException(
                        $"muse_reviewer_sandbox_unavailable: the Muse host reports sandbox availability "
                      + $"'{sandbox ?? "none"}', so the reviewer's read-only posture cannot be enforced.");

                LogSandboxUnavailable(ctx.AgentId, sandbox ?? "none");
            }

            if (relay && runtime.Session is { } session) {
                relayedStart = true;
                await _hookRelay(ctx.CapacitorPath, SessionStartPayload(session, ctx), session.SessionId, ctx.AgentId,
                    ctx.ServerUrl, ctx.DaemonId, ctx.DaemonEpoch, _logger, ct).ConfigureAwait(false);

                // The relay swallows its own cancellation.
                ct.ThrowIfCancellationRequested();
            }

            // The reviewer always has a prompt: BuildPlan refused a blank one.
            if (!string.IsNullOrEmpty(ctx.Prompt))
                await AwaitFirstTurnAsync(runtime, ctx.Prompt, ct).ConfigureAwait(false);

            return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: null);
        } catch (Exception ex) {
            if (runtime is not null) {
                await runtime.DisposeAsync().ConfigureAwait(false);
            } else {
                if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
                MuseHostedSessionMarker.Delete(_config.Home, plan.MintedSessionId);
            }

            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (ex is InvalidOperationException { Message: var m } && m.StartsWith("muse_", StringComparison.Ordinal)) throw;

            // stderr goes to the daemon log only: this exception is forwarded to the server.
            if (process?.Diagnostics is { Length: > 0 } stderr) {
                LogLaunchStderr(stderr);
                throw new InvalidOperationException($"{ex.Message} (stderr captured in daemon log)", ex);
            }

            throw;
        }
    }

    async Task AwaitFirstTurnAsync(MuseMspHostedAgentRuntime runtime, string prompt, CancellationToken ct) {
        var deadline = _readyDeadline ?? MuseMspHostedAgentRuntime.DefaultReadyDeadline;

        var send = runtime.SendFirstPromptAsync(prompt);

        // Abandoned on timeout or cancellation, then faulted by the runtime's disposal.
        _ = send.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try {
            await send.WaitAsync(deadline, _time, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new InvalidOperationException(
                $"muse_first_turn_unacknowledged: the Muse host did not acknowledge the first prompt within {deadline.TotalSeconds:0}s.");
        }
    }

    /// <summary>The lane's posture and the session to open. For a reviewer, validates the launch and
    /// builds its MCP servers under fresh per-launch aliases, so a workspace-declared server can never
    /// take the name an admitted tool is matched on.</summary>
    static MuseLaunchPlan BuildPlan(ref RuntimeStartContext ctx, DaemonConfig config) {
        var minted = ctx.ResumeSessionId is not { } resume ? MuseMsp.NewId()
            : Guid.TryParse(resume, out var id) ? id.ToString("D")
            : throw new InvalidOperationException($"muse_resume_session_invalid: '{resume}' is not a Muse session id.");

        if (!ctx.IsReviewFlow)
            return new MuseLaunchPlan(false, ":ask-me", "onRequest", null, ctx.ResumeSessionId is null ? null : minted,
                FrozenSet<string>.Empty, minted);

        // session/resume carries no permission profile, approval mode or MCP servers, so nothing would
        // re-apply the reviewer's posture to a resumed session.
        if (ctx.ResumeSessionId is not null)
            throw new InvalidOperationException(
                "muse_reviewer_resume_unsupported: a resumed Muse session cannot be given the reviewer's read-only "
              + "profile, approval mode or MCP servers, so a reviewer always starts a new session.");

        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath) || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "muse_reviewer_launch_context_incomplete: the result channel needs a server url, a kcap path and an agent id.");

        if (string.IsNullOrWhiteSpace(ctx.Prompt))
            throw new InvalidOperationException(
                "muse_reviewer_launch_context_incomplete: a reviewer needs a first prompt to run.");

        if (ctx.Prompt.AsSpan().TrimStart().StartsWith("/"))
            throw new InvalidOperationException(
                "muse_reviewer_prompt_is_command: a reviewer prompt may not begin with '/', which Muse runs as a command.");

        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var ids, out var rejected))
            throw new InvalidOperationException(
                $"muse_reviewer_allowlist_rejected: '{rejected}' is not an auto-approvable review-flow server.");

        var identity = LaunchIdentity.ForLaunch(aliasResultChannel: true);
        ctx = ctx with { LaunchIdentity = identity };

        var servers = AcpReviewFlowMcp.Build(ctx, ids);

        // Under denyUnmatched Muse resolves an MCP approval by policy before the daemon can answer it,
        // so the result channel could never be allowed; the daemon decides every approval itself.
        return new MuseLaunchPlan(true, ":read-only", "onRequest", RenderMcpServers(servers, NonDefaultConfigDir(config)),
            null, MuseUnattendedApprovals.AdmittedFor(servers, identity), minted);
    }

    /// <summary>Each server under <see cref="MuseUnattendedApprovals.ServerKey"/>, <c>required</c> so a
    /// reviewer that cannot reach its result channel fails at start instead of never reporting.</summary>
    static JsonObject RenderMcpServers(IReadOnlyList<AcpMcpServerSpec> servers, string? configDir) {
        var rendered = new JsonObject();

        foreach (var server in servers) {
            var env = new JsonObject();
            foreach (var variable in server.Env) env[variable.Name] = variable.Value;
            if (configDir is not null) env[ConfigRoot.ConfigDirEnvVar] = configDir;

            rendered[MuseUnattendedApprovals.ServerKey(server.Name)] = new JsonObject {
                ["transport"] = "stdio",
                ["command"]   = server.Command,
                ["args"]      = new JsonArray(server.Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["env"]       = env,
                ["mode"]      = "required",
            };
        }

        return rendered;
    }

    /// <summary>Muse gives an MCP child only <c>HOME</c> and the server's own <c>env</c>, so a kcap child
    /// derives its root from the home; a daemon whose root is elsewhere has to name it, fully qualified,
    /// because neither that child nor a hook runs in the daemon's working directory.</summary>
    static string? NonDefaultConfigDir(DaemonConfig config) {
        var root    = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ConfigRoot.Directory));
        var derived = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConfigRoot.UnderHome(config.Home.Path).Directory));

        return string.Equals(root, derived, StringComparison.Ordinal) ? null : root;
    }

    internal static ProcessStartInfo BuildPsi(DaemonConfig config, RuntimeStartContext ctx, bool reviewer) {
        // A BOM before the first JSON line would break the host's line parser.
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var psi = new ProcessStartInfo(config.MusePath, MuseLaunchArgs.For(config, ctx, reviewer)) {
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

        // The daemon id and epoch are what let a restarted daemon's orphan reaper recognise a survivor.
        psi.Environment[HostedAgent.AgentIdVar] = ctx.AgentId;
        HostedLaunchMarkers.Apply(psi.Environment, ctx);
        if (!string.IsNullOrEmpty(ctx.DaemonId))    psi.Environment["KCAP_DAEMON_ID"]        = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"]     = ctx.DaemonEpoch;
        if (!string.IsNullOrEmpty(ctx.ServerUrl))   psi.Environment[ProfileOverrides.UrlVar] = ctx.ServerUrl;

        return psi;
    }

    Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> RequestInteraction =>
        _permissionBroker is { } broker
            ? new AcpPermissionSurface(
                    broker, DaemonRunner.MuseVendor, _connection.RequestAcpInteractionAsync, _time,
                    _connection.ResolveAcpInteractionAsync, _permissionDecisionLog).RequestAsync
            : _connection.RequestAcpInteractionAsync;

    static JsonObject SessionStartPayload(MuseSessionDescriptor session, RuntimeStartContext ctx) => new() {
        ["hook_event_name"] = "SessionStart",
        ["source"]          = "startup",
        ["session_id"]      = session.SessionId,
        ["cwd"]             = ctx.Worktree.Path,
        ["transcript_path"] = session.LogPath,
        ["model"]           = session.ModelId,
        ["permission_mode"] = "default",
        ["model_provider"]  = "meta",
    };

    /// <summary>Relays the session's end when its start was relayed (the relayed hook then removes the
    /// marker); otherwise removes the marker itself, which Muse's own end hook may already have done.
    /// Awaited by the runtime's disposal, so a daemon shutting down does not lose the only end the
    /// session gets; the relay bounds itself.</summary>
    async Task OnRuntimeDisposedAsync(MuseMspHostedAgentRuntime? runtime, RuntimeStartContext ctx, MuseLaunchPlan plan, bool relayedStart) {
        if (!relayedStart || runtime?.Session is not { } session) {
            MuseHostedSessionMarker.Delete(_config.Home, plan.MintedSessionId);
            return;
        }

        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["reason"]          = "other",
            ["session_id"]      = session.SessionId,
            ["cwd"]             = ctx.Worktree.Path,
            ["transcript_path"] = session.LogPath,
        };

        try {
            await _hookRelay(ctx.CapacitorPath, payload, session.SessionId, ctx.AgentId, ctx.ServerUrl, ctx.DaemonId,
                ctx.DaemonEpoch, _logger, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Muse: relaying the session end failed (agentId={AgentId}).", ctx.AgentId);
        }
    }

    void SweepStaleMarkers() {
        try {
            MuseHostedSessionMarker.Sweep(_config.Home, _time, MarkerMaxAge);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: sweeping stale hosted-session markers failed.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Muse launch: agentId={AgentId} cwd={Cwd} reviewer={Reviewer}")]
    partial void LogLaunching(string agentId, string cwd, bool reviewer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Muse: the host reports sandbox availability '{Availability}'; the interactive agent runs without it (agentId={AgentId}).")]
    partial void LogSandboxUnavailable(string agentId, string availability);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Muse launch failed; captured stderr follows (not sent to server): {Diagnostics}")]
    partial void LogLaunchStderr(string diagnostics);
}
