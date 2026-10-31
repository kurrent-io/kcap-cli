using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

sealed class OutgoingRecorders(
        DaemonStore store, ConfigRoot config, ProfileContext profiles, UserHome home,
        HarnessRegistry harnesses, BinaryProbe binaries, WatcherManager watchers, TimeProvider time,
        StandardStreams console) : IOutgoingRecorders {
    static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(5);

    internal Func<bool> SystemdRunning { get; init; } = ServiceManagerFactory.SystemdRunning;

    /// <summary>A launch still starting is counted too: it is not in the agent table until its runtime answers. A
    /// daemon that cannot give that count, including one too old to report it, counts as busy.</summary>
    public async Task<IReadOnlyList<string>> BusyAsync() {
        var busy = new List<string>();

        foreach (var name in LiveDaemons()) {
            switch ((await LocalControlProbe.ProbeAsync(store, name, time, ProbeBudget)).Snapshot?.Busy) {
                case null:
                    busy.Add($"daemon '{name}': it cannot say whether agents are running");
                    break;
                case true:
                    busy.Add($"daemon '{name}' is running or starting agents");
                    break;
            }
        }

        return busy;
    }

    public async Task<bool> StopAsync() {
        var stopped        = true;
        var removedService = false;

        // The service goes first: `daemon stop` leaves a service-managed daemon alone, since a raw kill would be
        // restarted.
        try {
            var services = ServiceManagerFactory.ForCurrentOs(config, home, time, SystemdRunning);

            foreach (var id in services.ListInstalled()) {
                var ended = await ServiceRemoval.EndAgentsAsync(store, id, time);

                if (services.Uninstall(id, out var error)) {
                    if (!ended) DaemonCommands.MarkLedgerInterrupted(store, id, time, console.Error);
                    await console.Out.WriteLineAsync($"Removed daemon service '{id}' ({services.Describe()}).");
                    removedService = true;
                } else {
                    await console.Error.WriteLineAsync($"Could not remove daemon service '{id}': {error}");
                    stopped = false;
                }
            }
        } catch (PlatformNotSupportedException) {
            // Linux without systemd: a unit installed while it ran stays on disk, and would start a deleted binary
            // once systemd runs here again.
            if (OperatingSystem.IsLinux()) {
                IReadOnlyList<string> units;
                try {
                    units = new SystemdServiceManager(home).ListInstalled();
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    await console.Error.WriteLineAsync($"Could not list daemon services: {ex.Message}");
                    stopped = false;
                    units = [];
                }

                foreach (var id in units) {
                    await console.Error.WriteLineAsync(
                        $"Could not remove daemon service '{id}': systemd is not running. Delete {SystemdUnit.UnitPath(home, id)} to finish.");
                    stopped = false;
                }
            }
        }

        // After a service removal the bare "No daemons are running." of `daemon stop` would read as if
        // there had never been one.
        if (!Directory.Exists(store.Directory) || !Directory.EnumerateFiles(store.Directory, "*.pid").Any())
            await console.Out.WriteLineAsync(removedService ? "No other daemons are running." : "No daemons are running.");
        else if (await new DaemonCommands(store, config, profiles, home, harnesses, binaries, time, console).HandleAsync(["daemon", "stop", "--yes"]) != 0)
            stopped = false;

        if (await new CleanupCommand(watchers, console).HandleCleanup() != 0) stopped = false;

        foreach (var survivor in Live()) {
            await console.Error.WriteLineAsync($"{survivor} is still running.");
            stopped = false;
        }

        return stopped;
    }

    public string WatcherDirectory => watchers.GetWatcherDir();

    public IReadOnlyList<string> Live() => [
        .. LiveDaemons().Select(n => $"Daemon '{n}'"),
        .. LiveWatchers().Select(k => $"Watcher {k}"),
    ];

    IEnumerable<string> LiveDaemons() =>
        Directory.Exists(store.Directory)
            ? Directory.EnumerateFiles(store.Directory, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(n => n.Length > 0 && DaemonPidProbe.ValidatedPid(store, n) is not null)
                .Order()
            : [];

    IEnumerable<string> LiveWatchers() {
        var dir = watchers.GetWatcherDir();

        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(k => k.Length > 0 && watchers.PidAlive(k))
                .Order()
            : [];
    }
}
