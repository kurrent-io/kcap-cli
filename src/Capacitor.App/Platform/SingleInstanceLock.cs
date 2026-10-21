using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Platform;

public abstract class SingleInstanceLock : IDisposable {
    /// macOS enforces one copy of a bundle itself.
    public static SingleInstanceLock Unenforced { get; } = new UnenforcedSingleInstanceLock();

    /// Null when another copy holds the lock. That copy has been asked to show its window.
    public static SingleInstanceLock? Acquire(TimeSpan waitForRelease) {
        if (OperatingSystem.IsWindows()) return WindowsSingleInstanceLock.AcquireForThisExecutable(waitForRelease);
        if (OperatingSystem.IsLinux()) return LinuxSingleInstanceLock.AcquireForThisUser(waitForRelease);
        return Unenforced;
    }

    /// Raised on a background thread.
    public abstract event Action? ShowRequested;

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
