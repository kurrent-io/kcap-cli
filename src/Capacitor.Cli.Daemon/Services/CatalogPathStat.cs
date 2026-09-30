namespace Capacitor.Cli.Daemon.Services;

/// Fingerprint of a file a vendor's model catalog depends on. A missing file is a value of its
/// own, since deleting Pi's auth file does change what Pi can launch.
public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks) {
    internal static CatalogPathStat Of(string path) {
        try {
            var info = new FileInfo(path);
            return info.Exists ? new(path, true, info.Length, info.LastWriteTimeUtc.Ticks) : new(path, false, 0, 0);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new(path, false, 0, 0);
        }
    }
}
