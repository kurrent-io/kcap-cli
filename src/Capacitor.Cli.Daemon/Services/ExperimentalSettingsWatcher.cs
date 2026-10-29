using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Asks the orchestrator to re-resolve the experimental switches whenever <c>experimental.json</c>
/// changes, from the CLI, the desktop app or a hand edit alike. Events within the debounce window
/// share one refresh, since an atomic save raises several.
/// </summary>
internal sealed partial class ExperimentalSettingsWatcher : BackgroundService {
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    readonly string         _path;
    readonly Action<string> _refresh;
    readonly Func<bool>     _savedDiffersFromCommitted;
    readonly TimeProvider   _time;
    readonly ILogger        _logger;
    readonly TaskCompletionSource _watching = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExperimentalSettingsWatcher(
            DaemonConfig config, AgentOrchestrator orchestrator, ILogger<ExperimentalSettingsWatcher> logger, TimeProvider time)
        : this(new ExperimentalSettingsStore(config.ConfigRoot).Path,
            orchestrator.RefreshExperimentalSettings,
            () => config.ReadExperimentalSettings().IsEnabled(ExperimentalFeatures.CodexAppServer.Id) != config.CodexResolution.SwitchOn,
            time, logger) { }

    ExperimentalSettingsWatcher(string path, Action<string> refresh, Func<bool> savedDiffersFromCommitted, TimeProvider time, ILogger logger) {
        _path                      = path;
        _refresh                   = refresh;
        _savedDiffersFromCommitted = savedDiffersFromCommitted;
        _time                      = time;
        _logger                    = logger;
    }

    internal static ExperimentalSettingsWatcher ForTest(
            string path, Action<string> refresh, TimeProvider time, Func<bool>? savedDiffersFromCommitted = null) =>
        new(path, refresh, savedDiffersFromCommitted ?? (() => false), time, NullLogger.Instance);

    /// <summary>Completes once file events are being delivered, or once watching has been given up.</summary>
    internal Task Watching => _watching.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var directory = Path.GetDirectoryName(_path)!;
        var fileName  = Path.GetFileName(_path);

        using var debounce = _time.CreateTimer(
            _ => _refresh("experimental settings changed"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        FileSystemWatcher watcher;
        try {
            Directory.CreateDirectory(directory);
            watcher = new FileSystemWatcher(directory) {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            LogUnwatchable(_logger, directory, ex.Message);
            _watching.TrySetResult();
            return;
        }

        using (watcher) {
            void OnEvent(object? _, FileSystemEventArgs e) {
                if (Names(e, fileName)) debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error   += (_, _) => debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
            watcher.EnableRaisingEvents = true;
            _watching.TrySetResult();

            // A save between the startup resolution and here raised no event.
            if (_savedDiffersFromCommitted()) debounce.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

            try { await Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    static bool Names(FileSystemEventArgs e, string fileName) =>
        string.Equals(e.Name, fileName, StringComparison.Ordinal)
     || (e is RenamedEventArgs r && string.Equals(r.OldName, fileName, StringComparison.Ordinal));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot watch {Directory} for experimental settings ({Error}); a change applies on the next capability refresh")]
    static partial void LogUnwatchable(ILogger logger, string directory, string error);
}
