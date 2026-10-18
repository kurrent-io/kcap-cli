using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

sealed class OutgoingRecorders(
        DaemonStore store, ConfigRoot config, ProfileContext profiles, UserHome home,
        HarnessRegistry harnesses, BinaryProbe binaries, WatcherManager watchers, TimeProvider time) : IOutgoingRecorders {
    public async Task<IReadOnlyList<string>> BusyAsync() {
        var busy = new List<string>();

        foreach (var name in LiveDaemons()) {
            var socket = store.SocketPath(name);
            var agents = File.Exists(socket) ? await AgentCommand.FetchAgentsAsync(socket) : null;

            if (agents is null) {
                busy.Add($"daemon '{name}': its agents cannot be listed");
                continue;
            }

            var active = agents.Count(a => a.Status is "Starting" or "Running");
            if (active > 0) busy.Add($"daemon '{name}': {active} agent(s) running");
        }

        return busy;
    }

    public async Task<bool> StopAsync() {
        var stopped = true;

        // The service goes first: `daemon stop` leaves a service-managed daemon alone, since a raw kill would be
        // restarted.
        try {
            var services = ServiceManagerFactory.ForCurrentOs(config, home, time);

            foreach (var id in services.ListInstalled()) {
                if (services.Uninstall(id, out var error)) {
                    await Console.Out.WriteLineAsync($"  • Removed daemon service '{id}' ({services.Describe()})");
                } else {
                    await Console.Error.WriteLineAsync($"  • Could not remove daemon service '{id}': {error}");
                    stopped = false;
                }
            }
        } catch (PlatformNotSupportedException) {
            // No service backend on this OS.
        }

        if (await new DaemonCommands(store, config, profiles, home, harnesses, binaries, time).HandleAsync(["daemon", "stop", "--yes"]) != 0)
            stopped = false;

        if (await new CleanupCommand(watchers).HandleCleanup() != 0) stopped = false;

        foreach (var name in LiveDaemons()) {
            await Console.Error.WriteLineAsync($"  • Daemon '{name}' is still running.");
            stopped = false;
        }

        return stopped;
    }

    IEnumerable<string> LiveDaemons() =>
        Directory.Exists(store.Directory)
            ? Directory.EnumerateFiles(store.Directory, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(n => n.Length > 0 && DaemonPidProbe.ValidatedPid(store, n) is not null)
                .Order()
            : [];
}
