namespace Capacitor.Cli.Commands.Capture;

internal sealed record CaptureRepairFileSnapshot(string Path, long Length, DateTime LastWrite) {
    public static CaptureRepairFileSnapshot Take(string path) {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException($"Recovery source is missing: {path}");
        return new(path, file.Length, file.LastWriteTimeUtc);
    }

    public void AssertUnchanged() {
        var current = Take(Path);
        if (current != this) throw new IOException("Transcript changed during recovery; retry after the session ends.");
    }
}
