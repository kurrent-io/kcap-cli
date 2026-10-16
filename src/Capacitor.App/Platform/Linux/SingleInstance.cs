using System.Net.Sockets;
using System.Text;

namespace Capacitor.App.Platform.Linux;

/// One app per user where the platform does not enforce it: a second launch asks the running copy
/// to show its main window and exits. This is also the only way back to a window hidden to a tray
/// the desktop does not display.
public sealed class SingleInstance : IDisposable {
    const string LockName = "kcap-app.lock";
    const string SocketName = "kcap-app.sock";

    readonly FileStream _lock;
    readonly Socket _listener;

    SingleInstance(FileStream lockFile, Socket listener) {
        _lock = lockFile;
        _listener = listener;
    }

    /// A Unix socket path holds at most 107 bytes, so a deep directory cannot host the claim.
    public static bool FitsIn(string directory) => Encoding.UTF8.GetByteCount(Path.Join(directory, SocketName)) <= 107;

    /// Null when another copy holds the claim; that copy has been asked to show its window.
    public static SingleInstance? Claim(string directory, TimeSpan waitForRelease, TimeProvider time) {
        Directory.CreateDirectory(directory);
        var socketPath = Path.Join(directory, SocketName);
        var started = time.GetTimestamp();
        while (true) {
            if (TryLock(Path.Join(directory, LockName)) is { } held) {
                try {
                    File.Delete(socketPath);
                    var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                    listener.Listen();
                    return new SingleInstance(held, listener);
                } catch {
                    held.Dispose();
                    throw;
                }
            }
            if (time.GetElapsedTime(started) >= waitForRelease) {
                Signal(socketPath);
                return null;
            }
            Thread.Sleep(100);
        }
    }

    /// Runs <paramref name="activate"/> on the listener's thread once per later launch.
    public void Listen(Action activate) => _ = Task.Run(async () => {
        while (true) {
            try {
                using var client = await _listener.AcceptAsync();
            } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) {
                return;
            }
            activate();
        }
    });

    public void Dispose() {
        _listener.Dispose();
        _lock.Dispose();
    }

    // FileShare.None is an flock on Unix, released by the kernel however the holder exits.
    static FileStream? TryLock(string path) {
        try {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) {
            return null;
        }
    }

    // The holder may not have bound its socket yet; the launch is then dropped, not retried.
    static void Signal(string socketPath) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        } catch (SocketException) { }
    }
}
