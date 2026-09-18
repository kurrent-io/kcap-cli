using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit;

public class FirstRunNoticeTests {
    static (FirstRunNoticeStore Store, ConfigRoot Config) Fresh(TempDir dir) {
        var config = new ConfigRoot(dir.Path);

        return (new FirstRunNoticeStore(config), config);
    }

    [Test]
    public async Task Nothing_is_waiting_until_setup_arms_it() {
        using var dir = new TempDir();
        var (store, _) = Fresh(dir);

        await Assert.That(store.IsArmed()).IsFalse();
        await Assert.That(store.TryClaim()).IsFalse();

        store.Arm();

        await Assert.That(store.IsArmed()).IsTrue();
    }

    // The point of the marker: one session takes it, and no later session repeats it.
    [Test]
    public async Task The_notice_is_claimed_once() {
        using var dir = new TempDir();
        var (store, _) = Fresh(dir);
        store.Arm();

        await Assert.That(store.TryClaim()).IsTrue();
        await Assert.That(store.TryClaim()).IsFalse();
        await Assert.That(store.IsArmed()).IsFalse();
    }

    // Two sessions starting together both try; the rename decides, and only one notice is delivered.
    [Test]
    public async Task Concurrent_sessions_share_one_claim() {
        using var dir = new TempDir();
        var (store, _) = Fresh(dir);
        store.Arm();

        // A real race: every session waits for the lock rather than skipping on contention, so the
        // test asserts that exactly one takes it, not that the others happened to arrive late.
        var wait   = TimeSpan.FromSeconds(5);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.TryClaim(wait))));

        await Assert.That(claims.Count(c => c)).IsEqualTo(1);
    }

    [Test]
    public async Task A_second_setup_arms_it_again() {
        using var dir = new TempDir();
        var (store, _) = Fresh(dir);

        store.Arm();
        store.TryClaim();
        store.Arm();

        await Assert.That(store.TryClaim()).IsTrue();
    }

    [Test]
    public async Task The_emitter_takes_the_notice_and_names_the_tour() {
        using var dir = new TempDir();
        var (store, config) = Fresh(dir);
        store.Arm();

        var fragment = FirstRunNoticeEmitter.Resolve(optedOut: false, config);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("kcap-guided-tour");
        await Assert.That(FirstRunNoticeEmitter.Resolve(optedOut: false, config)).IsNull();
    }

    [Test]
    public async Task The_emitter_says_nothing_when_no_notice_is_waiting() {
        using var dir = new TempDir();
        var (_, config) = Fresh(dir);

        await Assert.That(FirstRunNoticeEmitter.Resolve(optedOut: false, config)).IsNull();
    }

    // Opting out must not consume the marker: turning the notice back on before the first session
    // should still deliver it, and a silent consume here would lose it for good.
    [Test]
    public async Task Opting_out_leaves_the_notice_where_it_is() {
        using var dir = new TempDir();
        var (store, config) = Fresh(dir);
        store.Arm();

        await Assert.That(FirstRunNoticeEmitter.Resolve(optedOut: true, config)).IsNull();
        await Assert.That(store.IsArmed()).IsTrue();
    }
}
