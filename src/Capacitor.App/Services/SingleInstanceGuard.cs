using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.App.Services;

/// One running copy per executable path and sign-in session on Windows, the parity of macOS
/// refusing a second copy of a bundle. A second launch asks the running copy to show its main
/// window and exits. A relaunch the app starts itself must pass RelaunchArg (Velopack's restart
/// after an update is recognised by its own hook): the old copy still holds the lock while it
/// shuts down, so the new one waits for it instead of handing over to a process that is exiting.
[SupportedOSPlatform("windows")]
public sealed partial class SingleInstanceGuard : IDisposable {
    public const string RelaunchArg = "--relaunch";

    readonly Mutex _mutex;
    readonly EventWaitHandle _showRequest;
    RegisteredWaitHandle? _registration;

    SingleInstanceGuard(Mutex mutex, EventWaitHandle showRequest) {
        _mutex = mutex;
        _showRequest = showRequest;
    }

    // The path is hashed because a kernel object name cannot contain a backslash.
    public static string NameFor(string executablePath) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath).ToUpperInvariant()));
        return $"kcap-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    /// Null when another copy kept the lock for the whole wait; that copy has been asked to show
    /// its window, and the caller should exit.
    public static SingleInstanceGuard? TryAcquire(string name, TimeSpan wait) {
        var mutex = new Mutex(false, $@"Local\{name}");
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.show");
        bool owned;
        try {
            owned = mutex.WaitOne(wait);
        } catch (AbandonedMutexException) {
            owned = true; // the previous owner died without releasing; the lock is ours now
        }
        if (owned) return new SingleInstanceGuard(mutex, showRequest);

        // The running copy is a background process, which Windows forbids from taking the
        // foreground unless the process the user just started grants it.
        AllowSetForegroundWindow(AsfwAny);
        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    /// The callback runs on a thread-pool thread.
    public void OnShowRequested(Action callback) {
        _registration?.Unregister(null);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showRequest, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // The mutex is not released here: ownership is thread-affine and Dispose may run on another
    // thread. Process exit abandons it, which the next TryAcquire treats as free.
    public void Dispose() {
        _registration?.Unregister(null);
        _showRequest.Dispose();
        _mutex.Dispose();
    }

    const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
