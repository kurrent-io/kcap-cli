namespace Capacitor.Cli.Commands;

/// <param name="StalePidFiles">PID files with no live process behind them — `kcap daemon doctor
/// --clean` removes these.</param>
public sealed record StatusDaemonJson(
    bool Running, IReadOnlyList<StatusDaemonEntryJson> Daemons, bool StalePidFiles);
