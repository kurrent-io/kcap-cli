using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalStackProbeTests {
    // Short: the Postgres socket path has to fit in sockaddr_un.
    [TempDir("lsp")] public required TempDir Tmp { get; init; }

    LocalRoot Root => new(Tmp.Path);

    [Test]
    public async Task An_empty_root_has_nothing_alive() {
        await Assert.That(LocalStackProbe.FindLive(Root)).IsNull();
    }

    [Test]
    public async Task A_held_server_lock_is_a_live_server() {
        if (OperatingSystem.IsWindows()) return;

        var path = Tmp.CreateFile(["run", LocalStackProbe.ServerLockName]);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.That(LocalStackProbe.FindLive(Root)).Contains("instance lock");

        await Assert.That(LocalStackProbe.FindLive(Root)).IsNull();
    }

    /// <summary>A killed postmaster leaves its pid file behind, naming a pid that is gone or now some other
    /// program; neither is a live Postgres.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_postmaster_pid_file_counts_only_while_a_postgres_runs_under_that_pid(bool pidOfThisProcess) {
        if (OperatingSystem.IsWindows()) return;

        var pid = pidOfThisProcess ? Environment.ProcessId : int.MaxValue;
        Tmp.CreateFile(["data", "postgres", "18", "postmaster.pid"], $"{pid}\n/data\n");

        await Assert.That(LocalStackProbe.FindLive(Root)).IsNull();
    }

    /// <summary>A socket file left behind by a killed postmaster refuses connections, and is not a live
    /// Postgres.</summary>
    [Test]
    public async Task Only_a_listening_postgres_socket_is_a_live_postgres() {
        if (OperatingSystem.IsWindows()) return;

        Tmp.CreateDir("run");
        var socketPath = Tmp.PathTo("run", LocalStackProbe.PostgresSocketName);

        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen();

            await Assert.That(LocalStackProbe.FindLive(Root)).Contains("Postgres");
        }

        // Disposing the listener unlinks its socket; a killed postmaster leaves the entry behind.
        if (!File.Exists(socketPath)) Tmp.CreateFile(["run", LocalStackProbe.PostgresSocketName]);

        await Assert.That(LocalStackProbe.FindLive(Root)).IsNull();
    }
}
