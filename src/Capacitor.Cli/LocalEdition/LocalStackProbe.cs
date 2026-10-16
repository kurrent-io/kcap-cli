using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Whether anything of the stack is still alive under a root. The event store runs inside the server, so
/// two checks cover it: the server's instance lock is free, and nothing accepts on Postgres's socket.
///
/// <para>Only meaningful once the unit is stopped: probing the lock takes it for a moment, and a server
/// starting in that moment would refuse to run.</para>
/// </summary>
static class LocalStackProbe {
    public const string ServerLockName     = "server.lock";
    public const string PostgresSocketName = ".s.PGSQL.5432";

    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(250);

    /// <summary>What is still alive, or null when nothing is.</summary>
    public static string? FindLive(LocalRoot root) {
        var run = Path.Combine(root.Directory, "run");

        if (LockHeld(Path.Combine(run, ServerLockName))) return "the server still holds its instance lock";
        if (Accepting(Path.Combine(run, PostgresSocketName))) return "Postgres still accepts connections on its socket";

        return null;
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
