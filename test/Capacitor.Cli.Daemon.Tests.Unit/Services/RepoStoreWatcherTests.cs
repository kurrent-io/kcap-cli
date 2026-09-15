using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The watcher that notices <c>repos.json</c> changing under a running daemon and re-sends the
/// repo paths, so a repo added from another process reaches the launch dialog without a restart.
/// </summary>
public class RepoStoreWatcherTests {
    static readonly RepoStoreFingerprint Before = new(100, 1);
    static readonly RepoStoreFingerprint After  = new(120, 2);

    sealed class Harness {
        public RepoStoreFingerprint? File;
        public RepoStoreFingerprint? Advertised;
        public bool                  Ready = true;
        public int                   Publishes;
        public Exception?            PublishThrow;
        public readonly RepoStoreWatcher Watcher;

        public Harness() {
            Watcher = RepoStoreWatcher.ForTest(
                stat: () => File,
                advertised: () => Advertised,
                isReady: () => Ready,
                publish: () => {
                    Publishes++;
                    if (PublishThrow is { } ex) return Task.FromException(ex);
                    Advertised = File;
                    return Task.CompletedTask;
                });
        }
    }

    [Test]
    public async Task An_unchanged_file_sends_nothing() {
        var h = new Harness { File = Before, Advertised = Before };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(0);
    }

    [Test]
    public async Task A_changed_file_is_sent_once() {
        var h = new Harness { File = After, Advertised = Before };

        await h.Watcher.TickAsync();
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_file_that_appears_is_sent() {
        var h = new Harness { File = Before, Advertised = null };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_file_that_disappears_is_sent() {
        var h = new Harness { File = null, Advertised = Before };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    // Registration sends the file as it is then; a send before it would only race it.
    [Test]
    public async Task Nothing_is_sent_until_the_daemon_is_registered() {
        var h = new Harness { File = After, Advertised = Before, Ready = false };

        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(0);

        h.Ready = true;
        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_send_is_retried_on_the_next_tick_and_never_escapes() {
        var h = new Harness { File = After, Advertised = Before, PublishThrow = new IOException("hub down") };

        await h.Watcher.TickAsync();
        h.PublishThrow = null;
        await h.Watcher.TickAsync();
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(2);
    }
}
