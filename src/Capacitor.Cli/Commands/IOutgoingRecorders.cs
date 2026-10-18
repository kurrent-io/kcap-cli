namespace Capacitor.Cli.Commands;

/// <summary>Every process that records for this machine's server: each daemon instance in the daemon store, its
/// OS service, and every watcher.</summary>
interface IOutgoingRecorders {
    /// <summary>One line per daemon that is running agents, or whose agents cannot be listed.</summary>
    Task<IReadOnlyList<string>> BusyAsync();

    /// <summary>Removes the daemon services and stops every daemon and watcher. False when any of them is still
    /// alive afterwards; what failed has been printed.</summary>
    Task<bool> StopAsync();
}
