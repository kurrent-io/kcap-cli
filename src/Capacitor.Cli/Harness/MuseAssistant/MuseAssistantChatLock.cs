namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>An exclusive open handle on a lock file. The kernel releases it when the holder dies, so
/// there is no stale-lock age and no takeover, which could admit two reclaimers.</summary>
public static class MuseAssistantChatLock {
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    public static FileStream? TryAcquire(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) {
            return null;
        }
    }

    public static async Task<FileStream?> AcquireAsync(string path, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
        var deadline = time.GetUtcNow() + timeout;

        while (true) {
            if (TryAcquire(path) is { } held) return held;
            if (time.GetUtcNow() >= deadline) return null;

            await Task.Delay(Poll, time, ct);
        }
    }
}
