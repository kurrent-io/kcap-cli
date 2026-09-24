using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A repo-path send records which version of <c>repos.json</c> the server now holds, and the
/// production watcher compares the file against exactly that record.
/// </summary>
public class ServerConnectionRepoPathsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class RepoPathsServerConnection(DaemonConfig config) : ServerConnection(
        config, UnusedTokenStore.Create(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        public readonly List<string[]>       Sent        = [];
        public readonly TaskCompletionSource SendEntered = new();
        public          TaskCompletionSource? SendGate;
        public          Exception?            SendThrow;
        public          bool                  Ready = true;

        internal override bool IsReady => Ready;

        internal override async Task SendRepoPathsAsync(string[] repoPaths) {
            Sent.Add(repoPaths);
            SendEntered.TrySetResult();
            if (Interlocked.Exchange(ref SendGate, null) is { } gate) await gate.Task;
            if (SendThrow is { } ex) throw ex;
        }
    }

    DaemonConfig NewConfig() => new() { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };

    [Test]
    public async Task A_send_carries_the_persisted_paths_and_records_the_file_it_read() {
        var store = new RepoPathStore(Config.Root, TimeProvider.System);
        await store.AddAsync("/tmp/project-a");
        await using var conn = new RepoPathsServerConnection(NewConfig());

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
        await Assert.That(conn.Sent[0]).IsEquivalentTo(new[] { Path.GetFullPath("/tmp/project-a") });
        await Assert.That(conn.AdvertisedRepoStore).IsEqualTo(store.Fingerprint());
    }

    [Test]
    public async Task A_failed_send_records_nothing() {
        await new RepoPathStore(Config.Root, TimeProvider.System).AddAsync("/tmp/project-a");
        await using var conn = new RepoPathsServerConnection(NewConfig()) { SendThrow = new IOException("hub down") };

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.AdvertisedRepoStore).IsNull();
    }

    /// An unreadable repos.json must not reach the server as an empty list stamped with the file's
    /// fingerprint: the watcher would then see nothing to resend, and the server would show no
    /// repositories until the file next changed.
    [Test]
    public async Task An_unreadable_list_is_not_sent_and_not_recorded() {
        await File.WriteAllBytesAsync(Config.PathTo("repos.json"), new byte[64]);
        await using var conn = new RepoPathsServerConnection(NewConfig());

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.Sent).IsEmpty();
        await Assert.That(conn.AdvertisedRepoStore).IsNull();
    }

    /// The daemon's own launch path and the watcher can send at the same time, and the server runs
    /// one client's invocations in parallel: an older list processed after a newer one, with the
    /// newer fingerprint recorded last, would leave the server stale with nothing left to repair it.
    [Test]
    public async Task Overlapping_sends_run_one_at_a_time() {
        var store = new RepoPathStore(Config.Root, TimeProvider.System);
        await store.AddAsync("/tmp/project-a");
        var gate = new TaskCompletionSource();
        await using var conn = new RepoPathsServerConnection(NewConfig()) { SendGate = gate };

        var first = conn.UpdateRepoPathsAsync();
        await conn.SendEntered.Task;
        await store.AddAsync("/tmp/project-b");
        var second = conn.UpdateRepoPathsAsync();
        // Waiting for a negative: the second send must not start while the first is held open.
        await Task.Delay(100);
        await Assert.That(conn.Sent.Count).IsEqualTo(1);

        gate.SetResult();
        await first;
        await second;

        await Assert.That(conn.Sent.Count).IsEqualTo(2);
        await Assert.That(conn.Sent[1]).IsEquivalentTo(new[] { Path.GetFullPath("/tmp/project-a"), Path.GetFullPath("/tmp/project-b") });
        await Assert.That(conn.AdvertisedRepoStore).IsEqualTo(store.Fingerprint());
    }

    /// The watcher as DI builds it reads the file the connection advertises: a write from
    /// another process is sent exactly once, and an untouched file is never re-sent.
    [Test]
    public async Task The_watcher_sends_a_write_from_another_process_exactly_once() {
        var config = NewConfig();
        await using var conn    = new RepoPathsServerConnection(config);
        using var       watcher = new RepoStoreWatcher(config, conn, NullLogger<RepoStoreWatcher>.Instance, TimeProvider.System);

        await watcher.TickAsync();
        await Assert.That(conn.Sent).IsEmpty();

        await new RepoPathStore(Config.Root, TimeProvider.System).AddAsync("/tmp/project-a");
        await watcher.TickAsync();
        await watcher.TickAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
    }
}
