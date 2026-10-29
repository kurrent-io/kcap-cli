using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// Whether the daemon's pid file names a live process. A token that cannot be compared counts as
/// live: the answer only decides whether the uninstall warns, and a needless warning is harmless.
public static class DaemonPidLiveness {
    /// Every daemon in the store whose pid file names a live process.
    public static IReadOnlyList<string> LiveDaemons(DaemonStore store) =>
        [.. store.EnumerateNames().Where(name => IsRunning(store, name))];

    public static bool IsRunning(DaemonStore store, string daemonName) {
        string[] lines;
        try {
            using var stream = new FileStream(store.PidPath(daemonName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }

        if (lines.Length == 0 || !int.TryParse(lines[0], out var pid) || pid <= 0) return false;
        if (lines.Length > 1 && ProcessStartToken.Matches(pid, lines[1]) is { } matched) return matched;

        try {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return false;
        }
    }
}
