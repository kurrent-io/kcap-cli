namespace Capacitor.Cli.Core.LocalIpc;

/// Per-daemon-name local control socket, colocated with the daemon's lock/pid files.
public static class LocalSocketPaths {
    public static string Socket(string daemonName)
        => Path.Combine(DaemonLockPaths.Directory, $"{DaemonLockPaths.Sanitize(daemonName)}.sock");
}
