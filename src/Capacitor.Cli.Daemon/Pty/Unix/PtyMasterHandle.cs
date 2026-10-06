using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Pty.Unix;

/// <summary>Owns a PTY master fd. Disposing marks it closed at once but defers the
/// <c>close(2)</c> until every holder of a reference has released it, so a write racing disposal
/// can never land on a closed descriptor the kernel has already handed to someone else.</summary>
sealed class PtyMasterHandle : SafeHandle {
    public PtyMasterHandle(int fd) : base(-1, ownsHandle: true) => SetHandle(fd);

    public override bool IsInvalid => handle == -1;

    /// <summary>Runs <paramref name="use"/> with the fd held open, or not at all once the handle is
    /// closed.</summary>
    public bool TryUse(Action<int> use) {
        var added = false;

        try {
            DangerousAddRef(ref added);
        } catch (ObjectDisposedException) {
            return false;
        }

        try {
            use((int)handle);

            return true;
        } finally {
            if (added) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle() => UnixPtyInterop.close((int)handle) == 0;
}
