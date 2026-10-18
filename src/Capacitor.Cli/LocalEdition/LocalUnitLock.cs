using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Serializes every check, install and removal of the per-user unit across installation
/// roots. The kernel drops the lock when the process dies, and the file is never deleted.</summary>
sealed class LocalUnitLock : IDisposable {
    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(100);

    /// <summary>How long every lifecycle command waits for another root's holder.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    readonly FileStream _lock;

    LocalUnitLock(FileStream @lock) => _lock = @lock;

    public static async Task<LocalUnitLock> AcquireAsync(UserHome home, TimeSpan wait, TimeProvider time, CancellationToken ct) {
        // Anchored at the home, not at KCAP_LOCAL_ROOT or KCAP_CONFIG_DIR: the unit is one per user,
        // so roots with different overrides must meet at one lock.
        var dir  = Path.Combine(home.Path, ".config", "kcap");
        var path = Path.Combine(dir, "local-unit.lock");

        DurableDirectory.Ensure(dir, home.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var deadline = time.GetUtcNow() + wait;
        var options  = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        while (true) {
            ct.ThrowIfCancellationRequested();

            try {
                return new(new FileStream(path, options));
            } catch (IOException) when (time.GetUtcNow() < deadline) {
                await Task.Delay(PollGap, time, ct);
            } catch (IOException ex) {
                throw new LocalLifecycleException(
                    "Another kcap is changing the local server unit. Wait for it to finish, then try again.", ex);
            }
        }
    }

    public void Dispose() => _lock.Dispose();
}
