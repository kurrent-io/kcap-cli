using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
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

    // What the notice may claim: that kcap is wired in, which the hook delivering it proves.
    // Never that this session reached the server - a rejected token has its own notice, and this
    // one would be the line contradicting it.
    [Test]
    public async Task The_notice_claims_setup_not_capture() {
        var fragment = FirstRunNoticeEmitter.Build(offerTour: true);

        await Assert.That(fragment).Contains("set up");
        await Assert.That(fragment).DoesNotContain("is recording");
    }

    // The tour reads through the kcap MCP servers, so where they are not registered it is not
    // something this user can be told to start.
    [Test]
    public async Task The_tour_is_offered_only_where_it_can_be_started() {
        await Assert.That(FirstRunNoticeEmitter.Build(offerTour: true)).Contains("kcap-guided-tour");
        await Assert.That(FirstRunNoticeEmitter.Build(offerTour: false)).DoesNotContain("kcap-guided-tour");
        // The rest of the notice stands on its own without it.
        await Assert.That(FirstRunNoticeEmitter.Build(offerTour: false)).Contains("first session");
    }

    [Test]
    public async Task Resolving_takes_the_notice_once() {
        using var dir = new TempDir();
        var (store, config) = Fresh(dir);
        store.Arm();
        var harnesses = TestHarnesses.All();

        await Assert.That(FirstRunNoticeEmitter.Resolve(false, config, HarnessId.Claude, harnesses)).IsNotNull();
        await Assert.That(FirstRunNoticeEmitter.Resolve(false, config, HarnessId.Claude, harnesses)).IsNull();
    }

    [Test]
    public async Task The_emitter_says_nothing_when_no_notice_is_waiting() {
        using var dir = new TempDir();
        var (_, config) = Fresh(dir);

        await Assert.That(FirstRunNoticeEmitter.Resolve(false, config, HarnessId.Claude, TestHarnesses.All())).IsNull();
    }

    // Opting out must not consume the marker: turning the notice back on before the first session
    // should still deliver it, and a silent consume here would lose it for good.
    [Test]
    public async Task Opting_out_leaves_the_notice_where_it_is() {
        using var dir = new TempDir();
        var (store, config) = Fresh(dir);
        store.Arm();

        await Assert.That(FirstRunNoticeEmitter.Resolve(true, config, HarnessId.Claude, TestHarnesses.All())).IsNull();
        await Assert.That(store.IsArmed()).IsTrue();
    }
}
