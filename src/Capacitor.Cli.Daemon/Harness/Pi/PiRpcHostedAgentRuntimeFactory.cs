using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for Pi's CLI (<c>pi --mode rpc</c>) over the LONG-LIVED
/// <see cref="PiRpcHostedAgentRuntime"/>: both an interactive hosted agent and an unattended
/// review-flow reviewer. A PR review (<c>ctx.IsReview</c>) and a borrowed workspace are refused.
///
/// <para><b>Not an ACP factory.</b> Pi speaks its own LF-framed JSONL-RPC over stdio for ONE
/// long-lived child that backs the whole hosted session (see <see cref="IPiRpcProcess"/>'s class doc
/// for the contrast with Antigravity's exec-per-turn shape), so this factory owns its own PSI builder
/// and process seam rather than reusing <see cref="AcpHostedAgentRuntimeFactory"/>.</para>
///
/// <para><b>The dual-capture gate.</b> Every launch this factory serves carries
/// <c>KCAP_PI_PURE=1</c> via <see cref="PiLaunchEnvironment.Apply"/> — see its class doc for why an
/// unconditional gate (not a reviewer-only one) is required: kcap's global Pi extension
/// (<c>~/.pi/agent/extensions/kcap.ts</c>) loads inside every <c>pi</c> process on the machine,
/// hosted or not, and would otherwise start a SECOND capture of a session this runtime already
/// records over the RPC wire.</para>
///
/// <para><b>No isolated <c>HOME</c>, unlike Antigravity/Kiro/Copilot.</b> An interactive launch
/// inherits the daemon's own <c>HOME</c>; a reviewer runs in a per-launch owner-only directory that
/// holds only its extension, manifest and session store, and still inherits the daemon's <c>HOME</c>
/// — so its result channel authenticates from the daemon's own token store and needs no brokered
/// delivery.</para>
///
/// <para><b>One gate ladder, read by advertisement and launch alike</b> (<see cref="ReviewerRefusal"/>):
/// consent, platform, binary presence and two version floors, so a daemon cannot advertise a reviewer
/// it would refuse to launch. Consent gates the reviewer only; an interactive launch never consults it.
/// A borrowed workspace fails closed to an owned worktree — there is no containment substrate here.</para>
/// </summary>
/// <param name="processSource">Test seam ONLY. Production passes null, which spawns the real
/// <see cref="PiRpcProcess"/> from the <see cref="ProcessStartInfo"/>
/// <see cref="BuildPsi(DaemonConfig, RuntimeStartContext)"/> built — so the seam changes nothing about
/// production behaviour, and the argv/env assertions run against the same builder a real launch
/// uses.</param>
/// <param name="binaryExists">Test seam ONLY, for <see cref="IsAvailable"/>. Production passes null,
/// which resolves the real binary through the registry's search path via
/// <see cref="Capacitor.Cli.Core.Setup.BinaryProbe.Finds"/>.</param>
/// <param name="readyDeadline">Test seam ONLY, threaded verbatim into every
/// <see cref="PiRpcHostedAgentRuntime"/> this factory constructs. Production passes null, which
/// falls through to <see cref="PiRpcHostedAgentRuntime.DefaultReadyDeadline"/> — so a test can bound
/// a silent-child launch to milliseconds instead of burning the real 30s default.</param>
/// <param name="resolveVersion">Test seam ONLY, for the installed-version half of the reviewer gate.
/// Production passes null, which spawns the configured binary to read its own reported version.</param>
/// <param name="posixHost">Test seam ONLY, for the platform half of the reviewer gate. Production
/// passes null, which reads the ambient OS. Taken as an argument so every arm of the ladder is
/// reachable from any host — the Windows arm is otherwise unassertable on POSIX.</param>
internal sealed partial class PiRpcHostedAgentRuntimeFactory(
        DaemonConfig                                                 config,
        ILoggerFactory                                                loggerFactory,
        TimeProvider                                                  time,
        Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>>? processSource = null,
        Func<string, bool>?                                           binaryExists = null,
        TimeSpan?                                                     readyDeadline = null,
        Func<string, string?>?                                        resolveVersion = null,
        bool?                                                         posixHost = null
    ) : IHostedAgentRuntimeFactory {
    readonly ILogger _logger = loggerFactory.CreateLogger<PiRpcHostedAgentRuntimeFactory>();

    readonly Func<ProcessStartInfo, CancellationToken, Task<IPiRpcProcess>> _processSource =
        processSource ?? ((psi, _) => Task.FromResult<IPiRpcProcess>(
            new PiRpcProcess(psi, loggerFactory.CreateLogger<PiRpcProcess>(), time)));

    readonly Func<string, bool> _binaryExists =
        binaryExists ?? (path => config.Binaries.Finds(path));

    readonly Func<string, string?> _resolveVersion =
        resolveVersion ?? (path => new VendorVersionResolver(config.Binaries).Resolve(path));

    readonly bool _posixHost = posixHost ?? !OperatingSystem.IsWindows();

    public string Vendor => "pi";

    public string CliPath => config.PiPath;

    public bool IsAvailable() => _binaryExists(config.PiPath);

    /// <summary>Advertising is the offer to review unattended, so it answers exactly what a launch would.</summary>
    public bool SupportsUnattended => DescribeUnattendedSupport().Supported;

    public UnattendedSupport DescribeUnattendedSupport() =>
        ReviewerRefusal() is { } reason ? new(false, reason) : new(true, null);

    /// <summary>Pi's model rides argv (<c>--model</c>), applied on every launch that resolves one —
    /// unlike a vendor whose model-selection hook is unverified.</summary>
    public bool SupportsModelSelection => true;

    /// <summary>The daemon-owned record of the oldest <c>pi</c> build this daemon will run — the same
    /// shared store the other gated reviewers use, keyed by vendor under this daemon's own state root.
    /// Read through here so the seeding path, the affirm verb and this gate cannot point at different
    /// files.</summary>
    internal static ReviewerVersionStore VersionStoreFor(DaemonConfig config) =>
        new(config.Store.StateDirectory(config.Name), DaemonRunner.PiVendor);

    /// <summary>Null when this daemon may launch a Pi reviewer, else the coded refusal. Consulted for
    /// advertisement and for a review-flow launch only; an interactive launch never asks. Consent and
    /// platform are decided with no probe and no file read — mirrors
    /// <see cref="Antigravity.AntigravityHostedAgentRuntimeFactory.LaunchRefusal"/>.</summary>
    internal string? ReviewerRefusal() {
        var beforeProbe = PiReviewerCapability.Decide(
            _posixHost, config.PiUnattendedReviewerEnabled, installedVersion: null, minimumVersion: null);

        if (beforeProbe != PiReviewerDecision.VersionUnresolved)
            return PiReviewerCapability.DenialReason(beforeProbe, null, null, config.PiPath);

        if (!IsAvailable())
            return $"pi_reviewer_binary_missing: '{config.PiPath}' does not resolve to an executable. Install "
                 + $"pi, or set {HarnessId.Pi.PathEnvVar} to its location.";

        var installed = _resolveVersion(config.PiPath);
        var minimum   = VersionStoreFor(config).Affirmed;
        var decision  = PiReviewerCapability.Decide(_posixHost, config.PiUnattendedReviewerEnabled, installed, minimum);

        return decision == PiReviewerDecision.Allowed
            ? null
            : PiReviewerCapability.DenialReason(decision, installed, minimum, config.PiPath);
    }

    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        // FIRST, ahead of every other refusal: no install or config makes a PR review work here — this
        // runtime hosts interactive agents and review-flow reviewers only, and a PR review needs the
        // `kcap mcp review` tool surface only the PTY launchers build.
        if (ctx.IsReview)
            throw new InvalidOperationException(
                "pi_pr_review_unsupported: this runtime hosts interactive agents and review-flow "
              + "reviewers only. A PR review needs the `kcap mcp review` tool surface and review prompt, "
              + "which only the PTY launchers build — launch the PR review with Claude.");

        // One ladder, read by advertisement and this launch alike, so the two cannot disagree.
        // Consulted for the reviewer shape only — an interactive launch never asks about consent or the
        // version floor. Defence in depth: the orchestrator's unattended gate runs first, but an
        // explicit `vendor: "pi"` request can reach a factory without consulting advertisement.
        if (ctx.IsReviewFlow && ReviewerRefusal() is { } refusal)
            throw new InvalidOperationException(refusal);

        // A property of this runtime, not of reviews: there is no sandbox substrate here, so nothing
        // bounds what a launch could read out of a checkout it does not own.
        if (ctx.Work != WorkLocation.OwnedWorktree)
            throw new InvalidOperationException(
                "pi_requires_owned_worktree: this runtime has no containment strategy for a borrowed "
              + "workspace, so it runs only in a daemon-owned worktree.");

        return ctx.IsReviewFlow
            ? await StartReviewerAsync(ctx, ct).ConfigureAwait(false)
            : await StartInteractiveAsync(ctx, ct).ConfigureAwait(false);
    }

    async Task<HostedRuntimeStart> StartInteractiveAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var psi = BuildPsi(config, ctx);

        LogLaunching(ctx.AgentId, ctx.Worktree.Path);

        var process = await _processSource(psi, ct).ConfigureAwait(false);

        PiRpcHostedAgentRuntime runtime;

        try {
            // Unopened until now — the orchestrator hands the factory an unopened journal so the
            // header's cwd/model are this launch's own.
            ctx.Journal?.Open(ctx.Worktree.Path, ResolveModel(config, ctx));

            runtime = new PiRpcHostedAgentRuntime(
                process,
                loggerFactory.CreateLogger<PiRpcHostedAgentRuntime>(),
                ctx.AgentId,
                ResolveModel(config, ctx),
                ctx.Worktree.Path,
                time,
                readyDeadline: readyDeadline,
                journal: ctx.Journal);
        } catch {
            // Construction itself failing (it should not, in practice) still leaves a spawned child —
            // no orphan pi processes.
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // MUST precede the first input: a clock assigned later makes every stamp inside the launch a
        // silent no-op, and a liveness sweep would then judge this agent from an empty record.
        //
        // Deferred, not fixed: the runtime's constructor starts its read pump and handshake tasks
        // immediately (before this line runs), so a response that arrives between construction and
        // this assignment would stamp the clock's SetLaunchStage against a still-null ActivityClock —
        // the identical race AntigravityHostedAgentRuntimeFactory's own post-construction
        // `runtime.ActivityClock = ctx.ActivityClock;` assignment carries, unfixed, today. Threading
        // the clock through the runtime's constructor instead (set before RunPumpAsync/
        // RunHandshakeAsync start) would close it, but would also touch every direct-construction
        // test site (PiRpcRuntimeFakes.NewRuntime, PiRpcHostedAgentRuntimeTests) for a window that
        // requires the real child to answer get_state before this single assignment statement runs —
        // sub-millisecond in practice. Left as-is, matching the sibling factory's accepted risk;
        // revisit both together if this is ever observed rather than theorized.
        runtime.ActivityClock = ctx.ActivityClock;

        try {
            if (!string.IsNullOrEmpty(ctx.Prompt))
                await runtime.SendUserInputAsync(ctx.Prompt).ConfigureAwait(false);

            // The ordering guarantee: the orchestrator reads transcript.AcpSessionId synchronously the
            // moment this method returns, so the barrier must resolve BEFORE that — see
            // PiRpcHostedAgentRuntime's rule (a).
            await runtime.WaitForSessionReadyAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) {
            // The launch is over either way; leaving the child alive would leave an unreapable agent
            // holding a daemon slot. DisposeAsync terminates the child and joins the pump/handshake.
            await runtime.DisposeAsync().ConfigureAwait(false);

            // A genuine shutdown propagates AS a cancellation — do not dress it up as a launch failure.
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;

            // The child's captured stderr is logged LOCALLY (never embedded in the exception message
            // — see DescribeLaunchFailure's doc) and this exception is what AgentOrchestrator forwards
            // verbatim to the server via LaunchFailedAsync.
            //
            // Bare `throw;` when there's nothing to add: it preserves ex's original stack trace,
            // where re-throwing ex itself (`throw ex;`) would reset it to here. Wrapping only
            // happens in the branch that actually adds information, and ex's own stack trace
            // survives intact as the wrapper's InnerException.
            if (DescribeLaunchFailure(ex, process.Diagnostics) is { } wrapped) throw wrapped;
            throw;
        }

        // The runtime IS the transcript source, so the orchestrator binds and forwards without
        // downcasting Runtime. McpConfigPath stays null: this launch writes no temp mcp-config file.
        return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
    }

    /// <summary>The unattended reviewer shape. Validation runs before anything is created; then the
    /// launch spawns into an owner-only directory, waits for readiness, confirms the live tool surface
    /// against the launch's list, sends the first prompt and reads the verdict. A verdict present at
    /// that final read means a guard ended the reviewer before it could report — surfaced as
    /// <see cref="PiReviewerReapedException"/>, whose message the orchestrator forwards as the launch
    /// failure.</summary>
    async Task<HostedRuntimeStart> StartReviewerAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var servers = ValidateReviewerContext(ref ctx);          // throws before anything is created
        var tools   = PiReviewerToolSurface.For(servers);
        var state   = config.Store.StateDirectory(config.Name);
        var paths   = PiReviewerLaunchDir.Create(
            state, config.DaemonEpoch ?? "unpinned", ctx.AgentId,
            PiReviewerManifest.Build(ctx.Worktree.Path, servers, tools), _logger);

        PiRpcHostedAgentRuntime? runtime = null;
        IPiRpcProcess?           process = null;

        try {
            LogLaunching(ctx.AgentId, ctx.Worktree.Path);
            process = await _processSource(BuildPsi(config, ctx, paths, tools), ct).ConfigureAwait(false);
            ctx.Journal?.Open(ctx.Worktree.Path, ResolveModel(config, ctx));

            runtime = new PiRpcHostedAgentRuntime(
                process, loggerFactory.CreateLogger<PiRpcHostedAgentRuntime>(), ctx.AgentId,
                ResolveModel(config, ctx), ctx.Worktree.Path, time,
                readyDeadline: readyDeadline,
                onDisposed:    () => PiReviewerLaunchDir.Delete(paths.Dir, state, _logger),
                journal:       ctx.Journal,
                reviewerGuards: new PiReviewerGuards(
                    TimeSpan.FromSeconds(config.PiReviewerTurnTimeoutSeconds), PiReviewerGuards.DefaultAbortGrace));
            runtime.ActivityClock = ctx.ActivityClock;

            // Ready first: Pi answers get_state only after the extension's factory and session_start
            // have run, so its readiness report is on disk by now.
            await runtime.WaitForSessionReadyAsync(ct).ConfigureAwait(false);

            if (PiReviewerReadiness.Verify(paths.Ready, tools) is { } mismatch)
                throw new InvalidOperationException(mismatch);

            if (!string.IsNullOrEmpty(ctx.Prompt))
                await runtime.SendUserInputAsync(ctx.Prompt).ConfigureAwait(false);

            if (runtime.ReadVerdict() is { } verdict)
                throw new PiReviewerReapedException(verdict.Reason, inner: null);

            return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
        } catch (Exception ex) {
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);   // also deletes the directory
            else {
                if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
                PiReviewerLaunchDir.Delete(paths.Dir, state, _logger);
            }

            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (ex is PiReviewerReapedException) throw;

            // Disposal awaited any claimed reap, so a verdict read here is final.
            if (runtime?.ReadVerdict() is { } reaped) throw new PiReviewerReapedException(reaped.Reason, ex);

            if (ex is InvalidOperationException { Message: var m } && m.StartsWith("pi_reviewer_", StringComparison.Ordinal)) throw;

            // Everything left is the child failing to come up — which, for a reviewer, is its extension
            // refusing to load. stderr is logged locally and never put in the message.
            if (process?.Diagnostics is { Length: > 0 } stderr) LogPiLaunchStderr(stderr);
            throw new InvalidOperationException(
                "pi_reviewer_extension_failed: the reviewer child did not start. Its extension refuses to load "
              + "when a result server or a required tool is unavailable (stderr is in the daemon log).", ex);
        }
    }

    /// <summary>The checks <see cref="AcpReviewFlowMcp.Build"/> assumes its caller made: a blank input
    /// or a rejected allowlist would launch a reviewer that can never report, and a first prompt Pi
    /// runs as a command would never reach the reviewer at all. Runs before the launch directory
    /// exists.</summary>
    static IReadOnlyList<AcpMcpServerSpec> ValidateReviewerContext(ref RuntimeStartContext ctx) {
        if (string.IsNullOrWhiteSpace(ctx.ServerUrl) || string.IsNullOrWhiteSpace(ctx.CapacitorPath) || string.IsNullOrWhiteSpace(ctx.AgentId))
            throw new InvalidOperationException(
                "pi_reviewer_launch_context_incomplete: the result channel needs a server url, a kcap path and an agent id.");

        if (ctx.Prompt is { } prompt && prompt.AsSpan().TrimStart().StartsWith("/"))
            throw new InvalidOperationException(
                "pi_reviewer_prompt_is_command: a reviewer prompt may not begin with '/', which Pi runs as a command.");

        if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(ctx.McpAllowlist, out var ids, out var rejected))
            throw new InvalidOperationException(
                $"pi_reviewer_allowlist_rejected: '{rejected}' is not an auto-approvable review-flow server.");

        // No alias: nothing the reviewed repository controls can register a Pi tool, so there is no
        // name to impersonate.
        ctx = ctx with { LaunchIdentity = LaunchIdentity.ForLaunch(aliasResultChannel: false) };

        return AcpReviewFlowMcp.Build(ctx, ids);
    }

    /// <summary>The effective model — the launch's own override, else the daemon-wide default. Same
    /// <c>"default"</c>-sentinel convention as <see cref="Antigravity.AntigravityHostedAgentRuntimeFactory.ResolveModel"/>
    /// and every other vendor's resolver.</summary>
    internal static string? ResolveModel(DaemonConfig config, RuntimeStartContext ctx) =>
        !string.IsNullOrEmpty(ctx.Model) && !string.Equals(ctx.Model, "default", StringComparison.OrdinalIgnoreCase)
            ? ctx.Model
            : config.PiModel;

    /// <summary>The interactive shape — no reviewer lane. See the four-argument overload for the full
    /// builder both shapes share.</summary>
    internal static ProcessStartInfo BuildPsi(DaemonConfig config, RuntimeStartContext ctx) =>
        BuildPsi(config, ctx, reviewer: null, tools: null);

    /// <summary>
    /// PURE builder for the whole launch — no process, no filesystem side effects. The real spawn
    /// path and the launch tests both go through it, so an assertion here certifies the vector the OS
    /// actually receives.
    ///
    /// <para>Interactive (<paramref name="reviewer"/> null): argv is exactly <c>--mode rpc</c> plus
    /// <c>--model &lt;m&gt;</c> when <see cref="ResolveModel"/> yields one. Reviewer (<paramref
    /// name="reviewer"/> non-null): argv additionally carries the extension, tool allowlist and
    /// containment flags a reviewer needs, in the byte-exact order the launch tests pin, and the env
    /// carries <see cref="PiLaunchEnvironment.ApplyReviewer"/>'s additions on top.</para>
    ///
    /// <para>Env always carries <see cref="PiLaunchEnvironment.Apply"/>'s <c>KCAP_PI_PURE=1</c>
    /// (never omitted — see that type's doc) plus the same daemon-identity stamps
    /// <see cref="Antigravity.AntigravityHostedAgentRuntimeFactory.BuildTurnPsi"/> carries:
    /// <c>KCAP_URL</c>, <c>KCAP_AGENT_ID</c>, <c>KCAP_DAEMON_ID</c>, <c>KCAP_DAEMON_EPOCH</c> — the
    /// last two are what makes a surviving child visible to <c>OrphanReaper</c>'s env-marker pass
    /// after a daemon restart, and are omitted (never stamped empty) when the context does not carry
    /// them, matching Antigravity's convention.</para>
    /// </summary>
    internal static ProcessStartInfo BuildPsi(DaemonConfig config, RuntimeStartContext ctx,
                                               PiReviewerLaunchPaths? reviewer, IReadOnlyList<PiReviewerTool>? tools) {
        var argv = new List<string> { "--mode", "rpc" };

        if (reviewer is not null) {
            // Order is pinned by test. --session-dir and --offline are containment, not hygiene: without the
            // first a repository's .pi/settings.json chooses where Pi writes, and without the second Pi
            // installs the operator's configured packages at startup.
            argv.AddRange([
                "--no-approve", "--no-extensions", "-e", reviewer.Extension,
                "--tools", PiReviewerToolSurface.AllowlistArg(tools!),
                "--no-context-files", "--no-skills", "--no-prompt-templates", "--no-themes",
                "--system-prompt", reviewer.SystemPrompt, "--append-system-prompt", "",
                "--offline",
                "--session-dir", reviewer.Sessions,
            ]);
        }

        if (ResolveModel(config, ctx) is { Length: > 0 } model) {
            argv.Add("--model");
            argv.Add(model);
        }

        // No-BOM UTF-8, matching AcpConnection's wire encoding: a BOM emitted before the first JSON
        // line would break pi's line parser, and the platform-default encoding these three
        // properties would otherwise fall back to is not guaranteed to be UTF-8 (nor BOM-free) on
        // every OS this daemon runs on.
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var psi = new ProcessStartInfo(config.PiPath, argv) {
            WorkingDirectory       = ctx.Worktree.Path,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = noBomUtf8,
            StandardOutputEncoding = noBomUtf8,
            StandardErrorEncoding  = noBomUtf8,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        // The dual-capture gate — unconditional, see PiLaunchEnvironment's doc.
        PiLaunchEnvironment.Apply(psi.Environment);
        if (reviewer is not null) PiLaunchEnvironment.ApplyReviewer(psi.Environment, reviewer.Manifest);

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) psi.Environment[ProfileOverrides.UrlVar] = ctx.ServerUrl;

        psi.Environment[HostedAgent.AgentIdVar] = ctx.AgentId;
        if (!string.IsNullOrEmpty(ctx.DaemonId))    psi.Environment["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) psi.Environment["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        return psi;
    }

    /// <summary>
    /// Logs the child's captured stderr to the DAEMON'S OWN log — never into the exception message
    /// that propagates out of this factory — and returns a safe wrapper around
    /// <paramref name="cause"/>, or <see langword="null"/> when there is no stderr to log.
    ///
    /// <para><b>Why stderr must never reach the returned exception.</b>
    /// <see cref="PiRpcProcess.Diagnostics"/> is a bounded capture of whatever the child wrote to
    /// stderr, and <c>PiRpcProcess.DrainStderrAsync</c> deliberately never logs that text itself
    /// because it can carry prompt fragments, paths, or auth detail. This exception, however, is
    /// what <c>AgentOrchestrator</c>'s launch catch forwards verbatim to the server via
    /// <c>LaunchFailedAsync</c> — an off-host sink. Embedding the raw capture in the message would
    /// defeat the exact boundary <c>DrainStderrAsync</c> exists to hold, so it is logged at Warning
    /// here (the daemon log is the access-controlled local sink) and the returned exception carries
    /// only <paramref name="cause"/>'s own generic reason plus an indicator that stderr was
    /// captured — never the stderr text. This mirrors the SAFER of the two vendor factories:
    /// <c>AntigravityHostedAgentRuntimeFactory.DescribeLaunchFailure</c> only ever uses its
    /// diagnostics capture to CLASSIFY a known failure shape (auth, via
    /// <c>LooksLikeAuthFailure</c>) and picks one of a few fixed, non-sensitive message templates —
    /// it never appends the raw capture either.</para>
    ///
    /// <para><paramref name="cause"/> is preserved as <see cref="Exception.InnerException"/>, stack
    /// trace and all, when this returns non-null; the caller falls back to a bare <c>throw;</c> of
    /// <paramref name="cause"/> itself when this returns <see langword="null"/>, so that unadorned
    /// case never has its stack trace reset.</para>
    /// </summary>
    Exception? DescribeLaunchFailure(Exception cause, string? diagnostics) {
        if (diagnostics is not { Length: > 0 }) return null;

        LogPiLaunchStderr(diagnostics);

        return new InvalidOperationException($"{cause.Message} (stderr captured in daemon log)", cause);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pi launch: agentId={AgentId} cwd={Cwd}")]
    partial void LogLaunching(string agentId, string cwd);

    /// <summary>The daemon-local-only sink for a failed launch's captured stderr — see
    /// <see cref="DescribeLaunchFailure"/>'s class doc for why this text must never reach the
    /// exception that propagates to the orchestrator/server.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Pi launch failed; captured stderr follows (not sent to server): {Diagnostics}")]
    partial void LogPiLaunchStderr(string diagnostics);
}
