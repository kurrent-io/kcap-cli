using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Polls the running binary; a size/mtime change queues a restart-after-update that
/// fires the chosen <see cref="IRestartStrategy"/> the moment the daemon is idle
/// (no hosted agents, no in-flight eval). Also handles explicit requests from the
/// control socket. Self-detection is macOS/Linux only (the poll is a no-op on Windows,
/// where a running binary can't be replaced); the explicit request path works anywhere.
/// </summary>
internal sealed partial class RestartCoordinator : BackgroundService {
    readonly string  _name;
    readonly string  _version;
    readonly ILogger _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<BinaryStat?> StatBinary = static () => null;
    internal Func<bool>        IsBusy     = static () => false;
    internal IRestartStrategy  Strategy;

    BinaryStat? _baseline;
    bool        _pending;
    bool        _force;
    bool        _fired;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Production constructor (DI).
    public RestartCoordinator(
            DaemonConfig config, AgentOrchestrator orchestrator, EvalContextCache evalCache,
            IRestartStrategy strategy, ILogger<RestartCoordinator> logger) {
        _name      = DaemonLockPaths.Sanitize(config.Name);
        _version   = DaemonRunner.ResolveDaemonVersion();
        _logger    = logger;
        Strategy   = strategy;
        IsBusy     = () => orchestrator.ActiveCount > 0 || evalCache.Count > 0;
        StatBinary = StatProcessBinary;
    }

    RestartCoordinator(string name, string version, IRestartStrategy strategy, ILogger logger) {
        _name = name; _version = version; Strategy = strategy; _logger = logger;
    }

    /// <summary>Test factory — bypasses DI; caller sets the seams.</summary>
    internal static RestartCoordinator ForTest(string name, string version, IRestartStrategy strategy) =>
        new(name, version, strategy, NullLogger.Instance);

    internal void PrimeBaseline() => _baseline = StatBinary();

    static BinaryStat? StatProcessBinary() {
        try {
            var p = Environment.ProcessPath;
            if (p is null) return null;
            var fi = new FileInfo(p);
            return fi.Exists ? new BinaryStat(fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        } catch {
            return null; // transient (file being swapped mid-install)
        }
    }

    /// <summary>Explicit restart request from the control socket. <paramref name="force"/> bypasses the idle gate.</summary>
    internal void RequestRestart(bool force) {
        _pending = true;
        _force  |= force;
        if (!force) {
            DaemonRestartMarker.Write(_name, new DaemonRestartMarker(_version, "requested", DateTimeOffset.UtcNow));
            LogQueued(_logger, "requested");
        }
    }

    /// <summary>One poll iteration (extracted for unit testing).</summary>
    internal void Tick() {
        if (_fired) return;

        if (!_pending && !OperatingSystem.IsWindows()) {
            var current = StatBinary();
            if (RestartDecision.BinaryChanged(_baseline, current)) {
                _pending  = true;
                _baseline = current;
                DaemonRestartMarker.Write(_name, new DaemonRestartMarker(_version, "self-detected", DateTimeOffset.UtcNow));
                LogQueued(_logger, "self-detected");
            }
        }

        if (RestartDecision.ShouldFire(_pending, IsBusy(), _force)) {
            _fired = true;
            Strategy.Restart();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        PrimeBaseline();
        // The successor of a previous restart: clear any stale marker, since our
        // running version now matches the on-disk binary.
        DaemonRestartMarker.Delete(_name);

        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update queued ({Reason}); will apply when idle")]
    static partial void LogQueued(ILogger logger, string reason);
}
