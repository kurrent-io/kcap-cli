using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Whether anything of the stack is still alive under a root. The event store runs inside the server, so
/// the server's instance lock covers it. Postgres is alive while it accepts on its socket, and also while
/// the postmaster its <c>postmaster.pid</c> names is running: a postmaster that is starting or recovering
/// accepts nothing yet.
///
/// <para>Only meaningful once the unit is stopped: probing the lock takes it for a moment, and a server
/// starting in that moment would refuse to run.</para>
/// </summary>
static class LocalStackProbe {
    public const string ServerLockName     = "server.lock";
    public const string PostgresSocketName = LocalSockets.PostgresName;

    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(250);

    /// <summary>What is still alive, or null when nothing is.</summary>
    public static string? FindLive(LocalRoot root) {
        var run = Path.Combine(root.Directory, "run");

        if (LockHeld(Path.Combine(run, ServerLockName))) return "the server still holds its instance lock";

        return FindLivePostgres(root);
    }

    /// <summary>Unlike <see cref="FindLive"/> this takes nothing, so it is safe while the server may be starting.</summary>
    public static string? FindLivePostgres(LocalRoot root) {
        if (Accepting(LocalSockets.Postgres(root))) return "Postgres still accepts connections on its socket";
        if (PostmasterRunning(PostmasterPidPath(root)) is { } pid) return $"the Postgres postmaster (pid {pid}) is still running";

        return null;
    }

    public static string PostmasterPidPath(LocalRoot root) =>
        Path.Combine(root.Directory, "data", "postgres", "18", "postmaster.pid");

    /// <summary>The pid on the file's first line, when a process by that pid runs a postgres executable. A file a
    /// killed postmaster left behind names a pid that is gone or reused by something else.</summary>
    static int? PostmasterRunning(string pidFile) {
        string? first;

        try {
            using var reader = new StreamReader(new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            first = reader.ReadLine();
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        }

        if (!int.TryParse(first?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            return null;

        try {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("postgres", StringComparison.Ordinal) ? pid : null;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return null;
        }
    }

    public static async Task WaitUntilDownAsync(
            LocalRoot root, TimeSpan budget, TimeProvider time, CancellationToken ct, Func<LocalRoot, string?>? findLive = null) {
        findLive ??= FindLive;
        var deadline = time.GetUtcNow() + budget;

        while (findLive(root) is { } live) {
            if (time.GetUtcNow() >= deadline)
                throw new LocalLifecycleException(
                    $"The local server's service stopped, but {live} under {root.Directory}. Nothing was removed.");

            await Task.Delay(PollGap, time, ct);
        }
    }

    static bool LockHeld(string path) {
        try {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        } catch (IOException) {
            return true;
        }
    }

    static bool Accepting(string socketPath) {
        if (!File.Exists(socketPath)) return false;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        } catch (SocketException) {
            return false;
        }
    }
}
