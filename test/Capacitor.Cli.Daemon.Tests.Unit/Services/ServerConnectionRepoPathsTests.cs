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
        config, UnusedTokenStore.Create(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance) {
        public readonly List<string[]> Sent = [];
        public          Exception?     SendThrow;
        public          bool           Ready = true;

        internal override bool IsReady => Ready;

        internal override Task SendRepoPathsAsync(string[] repoPaths) {
            Sent.Add(repoPaths);
            return SendThrow is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }

    DaemonConfig NewConfig() => new() { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };

    [Test]
    public async Task A_send_carries_the_persisted_paths_and_records_the_file_it_read() {
        var store = new RepoPathStore(Config.Root);
        await store.AddAsync("/tmp/project-a");
        await using var conn = new RepoPathsServerConnection(NewConfig());

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
        await Assert.That(conn.Sent[0]).IsEquivalentTo(new[] { Path.GetFullPath("/tmp/project-a") });
        await Assert.That(conn.AdvertisedRepoStore).IsEqualTo(store.Fingerprint());
    }

    [Test]
    public async Task A_failed_send_records_nothing() {
        await new RepoPathStore(Config.Root).AddAsync("/tmp/project-a");
        await using var conn = new RepoPathsServerConnection(NewConfig()) { SendThrow = new IOException("hub down") };

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.AdvertisedRepoStore).IsNull();
    }

    /// The watcher as DI builds it reads the file the connection advertises: a write from
    /// another process is sent exactly once, and an untouched file is never re-sent.
    [Test]
    public async Task The_watcher_sends_a_write_from_another_process_exactly_once() {
        var config = NewConfig();
        await using var conn    = new RepoPathsServerConnection(config);
        using var       watcher = new RepoStoreWatcher(config, conn, NullLogger<RepoStoreWatcher>.Instance);

        await watcher.TickAsync();
        await Assert.That(conn.Sent).IsEmpty();

        await new RepoPathStore(Config.Root).AddAsync("/tmp/project-a");
        await watcher.TickAsync();
        await watcher.TickAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
    }
}
