using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsSingleInstanceLock : SingleInstanceLock {
    readonly Mutex _mutex;
    readonly EventWaitHandle _showRequest;
    readonly RegisteredWaitHandle _registration;

    public override event Action? ShowRequested;

    WindowsSingleInstanceLock(Mutex mutex, EventWaitHandle showRequest) {
        _mutex = mutex;
        _showRequest = showRequest;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showRequest, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // The path is hashed because a kernel object name cannot contain a backslash.
    public static string NameFor(string executablePath) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath).ToUpperInvariant()));
        return $"kcap-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    public static SingleInstanceLock? AcquireForThisExecutable(TimeSpan waitForRelease) =>
        Environment.ProcessPath is { } exe ? TryAcquire(NameFor(exe), waitForRelease) : Unenforced;

    public static WindowsSingleInstanceLock? TryAcquire(string name, TimeSpan wait) {
        var mutex = new Mutex(false, $@"Local\{name}");
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.show");
        bool owned;
        try {
            owned = mutex.WaitOne(wait);
        } catch (AbandonedMutexException) {
            owned = true; // the previous owner died without releasing; the lock is ours now
        }
        if (owned) return new WindowsSingleInstanceLock(mutex, showRequest);

        // The running copy is a background process, which Windows forbids from taking the
        // foreground unless the process the user just started grants it.
        AllowSetForegroundWindow(AsfwAny);
        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    // The mutex is not released here: ownership is thread-affine and Dispose may run on another
    // thread. Process exit abandons it, which the next TryAcquire treats as free.
    public override void Dispose() {
        _registration.Unregister(null);
        _showRequest.Dispose();
        _mutex.Dispose();
        base.Dispose();
    }

    const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
