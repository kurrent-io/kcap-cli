using System.Security.Cryptography;

namespace Capacitor.Cli.Commands.Capture;

internal sealed record CaptureRepairFileSnapshot(string Path, long Length, DateTime LastWrite, string ContentHash) {
    public static CaptureRepairFileSnapshot Take(string path) {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException($"Recovery source is missing: {path}");
        var snapshot = new CaptureRepairFileSnapshot(path, file.Length, file.LastWriteTimeUtc, Hash(path));
        snapshot.AssertUnchanged(verifyContent: false);
        return snapshot;
    }

    public void AssertUnchanged(bool verifyContent = true) {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length != Length || file.LastWriteTimeUtc != LastWrite
            || verifyContent && Hash(Path) != ContentHash)
            throw new IOException("Transcript changed during recovery; retry after the session ends.");
    }

    static string Hash(string path) {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
