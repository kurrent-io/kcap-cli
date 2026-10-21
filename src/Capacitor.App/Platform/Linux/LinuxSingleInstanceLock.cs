using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

/// The only way back to a window hidden to a tray the desktop does not display.
public sealed class LinuxSingleInstanceLock : SingleInstanceLock {
    const string LockName = "kcap-app.lock";
    const string SocketName = "kcap-app.sock";
    // Chromium waits 20s. The answer comes off the UI thread, so only a stopped process misses it.
    static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ReleaseAfterEnd = TimeSpan.FromSeconds(5);

    readonly FileStream _lockFile;
    readonly Socket _listener;

    public override event Action? ShowRequested;

    LinuxSingleInstanceLock(FileStream lockFile, Socket listener) {
        _lockFile = lockFile;
        _listener = listener;
        _ = AnswerLaterLaunches();
    }

    // A broken runtime directory must not stop the app starting, only lose the guard.
    public static SingleInstanceLock? AcquireForThisUser(TimeSpan waitForRelease) {
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            : ConfigRoot.UnderHome(UserHome.FromEnvironment().Path).Directory;
        if (!FitsIn(directory)) {
            Console.Error.WriteLine($"kcap: single-instance check skipped: {directory} is too deep for its socket");
            return Unenforced;
        }
        try {
            return Acquire(directory, waitForRelease, TimeProvider.System, EndUnresponsive);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException) {
            Console.Error.WriteLine($"kcap: single-instance check skipped: {ex.Message}");
            return Unenforced;
        }
    }

    /// A holder that does not answer is handed to <paramref name="endUnresponsive"/> and its lock taken.
    public static LinuxSingleInstanceLock? Acquire(string directory, TimeSpan waitForRelease, TimeProvider time, Action<int> endUnresponsive) {
        Directory.CreateDirectory(directory);
        var lockPath = Path.Join(directory, LockName);
        var socketPath = Path.Join(directory, SocketName);

        if (WaitForLock(lockPath, waitForRelease, time) is { } held) return Own(held, socketPath);
        if (SilentHolder(socketPath) is not { } pid) return null;
        endUnresponsive(pid);
        return WaitForLock(lockPath, ReleaseAfterEnd, time) is { } released ? Own(released, socketPath) : null;
    }

    /// A Unix socket path holds at most 107 bytes, so a deep directory cannot host the lock.
    public static bool FitsIn(string directory) => Encoding.UTF8.GetByteCount(Path.Join(directory, SocketName)) <= 107;

    async Task AnswerLaterLaunches() {
        while (true) {
            try {
                using var client = await _listener.AcceptAsync();
                await client.SendAsync(new byte[] { 1 });
                ShowRequested?.Invoke();
            } catch (ObjectDisposedException) {
                return;
            } catch (SocketException) when (_listener.SafeHandle.IsClosed) {
                return;
            } catch (SocketException) { }
        }
    }

    public override void Dispose() {
        _listener.Dispose();
        _lockFile.Dispose();
        base.Dispose();
    }

    static LinuxSingleInstanceLock Own(FileStream held, string socketPath) {
        try {
            File.Delete(socketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen();
            return new LinuxSingleInstanceLock(held, listener);
        } catch {
            held.Dispose();
            throw;
        }
    }

    static FileStream? WaitForLock(string path, TimeSpan wait, TimeProvider time) {
        var started = time.GetTimestamp();
        while (true) {
            if (TryLock(path) is { } held) return held;
            if (time.GetElapsedTime(started) >= wait) return null;
            Thread.Sleep(100);
        }
    }

    // FileShare.None is an flock on Unix. A held lock surfaces as an IOException like any other open
    // failure, and every managed open takes a shared flock, so the file itself cannot be probed. A
    // directory that still takes a file had only the lock in the way; any other failure propagates
    // so the caller drops the guard instead of taking it for a running copy.
    static FileStream? TryLock(string path) {
        try {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) when (TakesFiles(Path.GetDirectoryName(path)!)) {
            return null;
        }
    }

    static bool TakesFiles(string directory) {
        var probe = Path.Combine(directory, $".kcap-app.{Guid.NewGuid():N}");
        try {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    // Null once the holder answers, or when it has not bound its socket yet. The kernel completes a
    // connect into the listen backlog for a stopped holder too, so only the answer proves it runs.
    static int? SilentHolder(string socketPath) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        // Before connecting: on macOS setsockopt fails with EINVAL once the holder has answered and closed.
        socket.ReceiveTimeout = (int)AnswerTimeout.TotalMilliseconds;
        try {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        } catch (SocketException) {
            return null;
        }
        try {
            if (socket.Receive(new byte[1]) == 1) return null;
        } catch (SocketException) { }
        return PeerPid(socket);
    }

    // SO_PEERCRED on the connecting side carries the listener's credentials: { pid, uid, gid }.
    static int? PeerPid(Socket socket) {
        if (!OperatingSystem.IsLinux()) return null;
        Span<byte> cred = stackalloc byte[12];
        try {
            return socket.GetRawSocketOption(1, 17, cred) == 12 ? BitConverter.ToInt32(cred) : null;
        } catch (SocketException) {
            return null;
        }
    }

    static void EndUnresponsive(int pid) {
        Console.Error.WriteLine($"kcap: ending a running copy that did not respond (pid {pid})");
        try {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }
}
