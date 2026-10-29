using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Outcome of a restart request, used to ack the control-socket caller honestly.</summary>
internal enum RestartRequestResult {
    /// <summary>The restart is firing now (daemon shutting down / respawning).</summary>
    Restarting,
    /// <summary>Accepted but deferred — the daemon is busy; it will restart when idle.</summary>
    Queued,
    /// <summary>A restart was attempted but failed to start; it will be retried.</summary>
    Failed,
    /// <summary>Foreground daemon — no auto-restart; the user must restart it manually.</summary>
    ManualRequired,
}

/// <summary>
/// Polls the running binary; a size/mtime change queues a restart-after-update that
/// fires the chosen <see cref="IRestartStrategy"/> the moment the daemon is idle
/// (no in-flight eval, launch in admission or mid-turn flow participant, and interactive agents at a turn
/// boundary). Also handles explicit requests from the control socket; a when-idle request waits for no
/// agents at all. A vendor CLI installed after startup queues a when-idle restart too: what the daemon can
/// host is decided once at boot, so only a restart can advertise it. On Windows self-detection covers
/// script installs only; the explicit request path works anywhere.
/// </summary>
internal sealed partial class RestartCoordinator : BackgroundService {
    readonly TimeProvider _time;
    readonly DaemonStore _store;
    readonly string      _name;
    readonly string  _version;
    readonly ILogger _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<BinaryStat?> StatBinary = static () => null;
    // The binary as it stood when this process started. An update that replaces it while the daemon is still
    // booting lands before the poll begins, so a baseline taken then would already be the new binary.
    internal BinaryStat? LaunchBinary;
    internal Func<bool>        IsBusy     = static () => false;
    // Busy for a forced restart, or a self-detected one past TurnWait: evals and launches in
    // admission, not flow participants.
    internal Func<bool>        IsHardBusy = static () => false;
    internal Func<bool>        HasAgents  = static () => false;
    internal Func<bool>        AtTurnBoundary = static () => true;
    internal Func<bool>        HarnessInstalled = static () => false;
    internal TimeSpan          TurnWait   = TimeSpan.FromMinutes(5);
    internal IRestartStrategy  Strategy;

    // _gate guards all mutable state below. RequestRestart runs on the control-socket
    // handler thread while the poll loop runs on the timer thread, so the flags must be
    // synchronized — plain fields would risk stale reads / lost updates.
    readonly Lock _gate = new();
    BinaryStat?   _baseline;
    bool          _pending;
    bool          _force;
    bool          _whenIdle;
    bool          _fired;
    bool          _selfDetected;
    // Pending only for a newly installed harness, so an update landing meanwhile still takes over.
    bool          _harnessOnly;
    // A foreground daemon cannot restart itself; the harness check stops there rather than holding the
    // coordinator fired, which would swallow every later update and request.
    bool          _harnessManual;
    DateTimeOffset _pendingSince;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Production constructor (DI).
    public RestartCoordinator(
            DaemonConfig config, AgentOrchestrator orchestrator, EvalContextCache evalCache,
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories,
            IRestartStrategy strategy, ILogger<RestartCoordinator> logger, TimeProvider time) {
        _time      = time;
        TurnWait   = config.RestartTurnWait;
        _store     = config.Store;
        _name      = config.Name;
        _version   = DaemonRunner.ResolveDaemonVersion();
        _logger    = logger;
        Strategy   = strategy;
        WireGate(orchestrator, evalCache);
        StatBinary = StatProcessBinary;
        LaunchBinary = config.LaunchBinary;
        HarnessInstalled = () => UnadvertisedNowAvailable(config.SupportedVendors, factories.Values);
    }

    /// <summary>Whether a factory the daemon did not advertise now finds its CLI. Null advertisement means
    /// startup has not probed yet, so nothing can have been installed since.</summary>
    internal static bool UnadvertisedNowAvailable(string[]? advertised, IEnumerable<IHostedAgentRuntimeFactory> factories) =>
        advertised is not null
     && factories.Any(f => !advertised.Contains(f.Vendor, StringComparer.Ordinal) && f.IsAvailable());

    RestartCoordinator(
            DaemonStore store, string daemonName, string version, IRestartStrategy strategy, ILogger logger,
            TimeProvider time) {
        _time      = time;
        _store = store; _name = daemonName; _version = version; Strategy = strategy; _logger = logger;
    }

    /// <summary>Test factory — bypasses DI; caller sets the seams.</summary>
    internal static RestartCoordinator ForTest(
            DaemonStore store, string daemonName, string version, IRestartStrategy strategy, TimeProvider time) =>
        new(store, daemonName, version, strategy, NullLogger.Instance, time);

    void WireGate(AgentOrchestrator orchestrator, EvalContextCache evalCache) {
        IsHardBusy     = () => evalCache.Count > 0 || orchestrator.LaunchesInAdmission > 0;
        IsBusy         = () => IsHardBusy() || orchestrator.FlowParticipantsMidRound > 0;
        AtTurnBoundary = () => orchestrator.InteractiveAgentsAtTurnBoundary;
        HasAgents      = () => orchestrator.ActiveCount > 0;
    }

    /// <summary>Test factory with the production gate wiring over a real orchestrator.</summary>
    internal static RestartCoordinator ForTest(
            DaemonStore store, string daemonName, string version, IRestartStrategy strategy, TimeProvider time,
            AgentOrchestrator orchestrator, EvalContextCache evalCache) {
        var c = new RestartCoordinator(store, daemonName, version, strategy, NullLogger.Instance, time);
        c.WireGate(orchestrator, evalCache);

        return c;
    }

    internal void PrimeBaseline() {
        lock (_gate) _baseline = LaunchBinary ?? StatBinary();
    }

    internal static BinaryStat? StatProcessBinary() => StatBinaryAt(Environment.ProcessPath, OperatingSystem.IsWindows(), File.Exists);

    /// <summary>
    /// What the poll compares: the binary reached through a script install's or a snap's <c>current</c>,
    /// which an update switches rather than overwriting this file, plus where that link points. Outside a
    /// script install Windows never replaces a running binary in place, so there it returns null and
    /// BinaryChanged() never fires; explicit restart requests still work.
    /// </summary>
    internal static BinaryStat? StatBinaryAt(string? processPath, bool isWindows, Func<string, bool> fileExists) {
        if (processPath is null) return null;
        var layout = ScriptInstallLayout.FromBinary(processPath, fileExists);
        if (isWindows && layout is null) return null;

        try {
            var fi     = new FileInfo(InstallPaths.Stabilize(processPath, fileExists));
            var target = CurrentTarget(processPath, layout, dir => new DirectoryInfo(dir).LinkTarget);
            return fi.Exists ? new BinaryStat(fi.Length, fi.LastWriteTimeUtc.Ticks, target) : null;
        } catch {
            return null; // transient (file being swapped mid-install)
        }
    }

    /// <summary>Where the install's <c>current</c> link points, or null outside a script install or a snap.
    /// A refresh can leave the binary's size and timestamp unchanged, so the revision it names is what
    /// tells two snap revisions apart.</summary>
    internal static string? CurrentTarget(string processPath, ScriptInstallLayout? layout, Func<string, string?> linkTarget) =>
        layout is not null ? linkTarget(layout.Current)
        : SnapLayout.FromBinary(processPath) is { } snap ? linkTarget(snap.Current)
        : null;

    /// <summary>
    /// Explicit restart request from the control socket. <paramref name="force"/> bypasses
    /// the idle gate; <paramref name="whenIdle"/> also waits for every agent to be gone. Evaluates immediately (rather than waiting for the next poll tick) so
    /// a manual restart of an already-idle daemon is prompt, and returns the result so the
    /// caller can ack honestly.
    /// </summary>
    internal RestartRequestResult RequestRestart(bool force, bool whenIdle = false) {
        lock (_gate) {
            // A pending update already restarts at a turn boundary; waiting for every agent would only delay it.
            var keepUpdate = whenIdle && !force && _pending && _selfDetected;

            if (!keepUpdate) {
                _pending      = true;
                _selfDetected = false;
                _harnessOnly  = false;
                _whenIdle     = whenIdle;
                if (force) _force = true;

                if (!force) {
                    DaemonRestartMarker.Write(_store, _name, new DaemonRestartMarker(_version, "requested", _time.GetUtcNow()));
                    LogQueued(_logger, "requested");
                }
            }
        }

        return Evaluate();
    }

    /// <summary>One poll iteration (timer-driven; also the unit-test entry point).</summary>
    internal void Tick() => Evaluate();

    /// <summary>
    /// Detect a binary change (if not already pending), then fire the strategy if the gate
    /// allows. The fire decision claims <c>_fired</c> under the lock for single-fire safety,
    /// but the (potentially blocking) strategy runs outside the lock. A <see cref="RestartOutcome.Retry"/>
    /// un-claims so a failed respawn is retried on the next tick/request.
    /// </summary>
    RestartRequestResult Evaluate() {
        bool fire;
        bool harnessFire;

        lock (_gate) {
            if (_fired) return RestartRequestResult.Restarting; // already in progress

            if (!_pending || _harnessOnly) {
                var current = StatBinary();
                if (RestartDecision.BinaryChanged(_baseline, current)) {
                    _pending      = true;
                    _selfDetected = true;
                    _harnessOnly  = false;
                    _whenIdle     = false;
                    _pendingSince = _time.GetUtcNow();
                    _baseline     = current;
                    DaemonRestartMarker.Write(_store, _name, new DaemonRestartMarker(_version, "self-detected", _time.GetUtcNow()));
                    LogQueued(_logger, "self-detected");
                }
            }

            if (!_pending && !_harnessManual && HarnessInstalled()) {
                _pending     = true;
                _harnessOnly = true;
                _whenIdle    = true;
                LogHarnessInstalled(_logger);
            }

            var waitExpired = _selfDetected && _time.GetUtcNow() - _pendingSince >= TurnWait;
            // Past the wait bound a mid-round flow participant no longer holds the restart; the
            // shutdown ends or parks it.
            var busy = _force || waitExpired ? IsHardBusy() : _whenIdle ? IsHardBusy() || HasAgents() : IsBusy();
            fire = RestartDecision.ShouldFire(_pending, busy, _force, _selfDetected, AtTurnBoundary(), waitExpired);
            if (fire) _fired = true; // claim before releasing the lock (single-fire)
            harnessFire = _harnessOnly;
        }

        if (!fire) return RestartRequestResult.Queued;

        var outcome = Strategy.Restart();

        if (outcome == RestartOutcome.NoOp && harnessFire) {
            lock (_gate) {
                (_fired, _harnessManual) = (false, true);
                // A request that arrived while the strategy ran replaced the harness one and stays pending.
                if (_harnessOnly) (_pending, _harnessOnly, _whenIdle) = (false, false, false);
            }
            return RestartRequestResult.ManualRequired;
        }

        if (outcome == RestartOutcome.Retry) {
            lock (_gate) _fired = false; // un-claim so a later tick/request retries
            return RestartRequestResult.Failed;
        }

        return outcome == RestartOutcome.NoOp
            ? RestartRequestResult.ManualRequired
            : RestartRequestResult.Restarting;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        PrimeBaseline();
        // The successor of a previous restart: clear any stale marker, since our
        // running version now matches the on-disk binary.
        DaemonRestartMarker.Delete(_store, _name);

        using var timer = new PeriodicTimer(PollInterval, _time);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update queued ({Reason}); will apply when idle")]
    static partial void LogQueued(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "A coding agent CLI was installed; restart queued to advertise it once no agents are running")]
    static partial void LogHarnessInstalled(ILogger logger);
}
