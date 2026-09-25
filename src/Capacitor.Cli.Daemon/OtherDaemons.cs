using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon;

/// Whether any daemon other than this one is running on the machine, read from the lock each holds
/// exclusively for its lifetime: a lock file that cannot be opened exclusively belongs to a live
/// daemon. Probing opens the file and closes it at once, never creating or deleting it.
static class OtherDaemons {
    public static bool AnyAlive(DaemonStore store, string selfName) {
        if (!Directory.Exists(store.Directory)) return false;

        var self = store.LockPath(selfName);
        foreach (var lockPath in Directory.EnumerateFiles(store.Directory, "*.lock")) {
            if (string.Equals(Path.GetFullPath(lockPath), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) continue;
            if (IsHeld(lockPath)) return true;
        }

        return false;
    }

    internal static bool IsHeld(string lockPath) {
        try {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        } catch (FileNotFoundException) {
            return false;
        } catch (IOException) {
            return true;
        } catch (UnauthorizedAccessException) {
            return true;
        }
    }
}
