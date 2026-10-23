namespace Capacitor.Cli.Core.Resume;

/// <summary>An exclusive hold on one ledger entry, shared by the daemon and every kcap process that ends
/// a hosted session: committing an end and marking the agent for resume must never both succeed.</summary>
public sealed class ResumeEntryLock : IDisposable {
    static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    readonly FileStream _stream;

    ResumeEntryLock(FileStream stream) => _stream = stream;

    public static string PathFor(string entryPath) => entryPath + ".lock";

    /// <summary>Null when another holder kept it for all of <paramref name="wait"/>.</summary>
    public static ResumeEntryLock? TryAcquire(string entryPath, TimeSpan wait) {
        var path     = PathFor(entryPath);
        var attempts = Math.Max(1, (int) Math.Ceiling(wait / Retry));
        var options  = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        for (var attempt = 1; ; attempt++) {
            try {
                return new ResumeEntryLock(new FileStream(path, options));
            } catch (IOException) when (attempt < attempts) {
                Thread.Sleep(Retry);
            } catch (IOException) {
                return null;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
