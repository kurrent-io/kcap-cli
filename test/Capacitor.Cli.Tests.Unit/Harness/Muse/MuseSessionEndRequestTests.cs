using Capacitor.Cli.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Harness.Muse;

public class MuseSessionEndRequestTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "01a13694b0ad7ae28bf52af6357a0847";

    [Test]
    public async Task A_written_request_is_claimed_once_and_gone_once_released() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, """{"session_id":"x","reason":"exit"}""");
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsTrue();

        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher");
        await Assert.That(claimed).IsNotNull();
        await Assert.That(claimed!.Payload).IsEqualTo("""{"session_id":"x","reason":"exit"}""");
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();

        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "hook")).IsNull();

        MuseSessionEndRequest.Release(claimed.ClaimedPath);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
    }

    [Test]
    public async Task An_unclaimed_request_is_there_for_the_next_taker_unless_a_newer_one_arrived() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"first"}""");
        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "w1")!;

        MuseSessionEndRequest.Unclaim(paths, SessionId, claimed);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w2")!.Payload).IsEqualTo("""{"reason":"first"}""");

        var stale = MuseSessionEndRequest.TryClaim(paths, SessionId, "w3");
        await Assert.That(stale).IsNull();

        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"old"}""");
        var old = MuseSessionEndRequest.TryClaim(paths, SessionId, "w4")!;
        MuseSessionEndRequest.Write(paths, SessionId, """{"reason":"newer"}""");

        MuseSessionEndRequest.Unclaim(paths, SessionId, old);
        await Assert.That(File.Exists(old.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "w5")!.Payload).IsEqualTo("""{"reason":"newer"}""");
    }

    [Test]
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }

    /// <summary>The hand-over time, not the claim time, is what a resume is measured against: a claim
    /// taken late still carries an end older than the session's new life.</summary>
    [Test]
    public async Task A_resume_after_the_hand_over_makes_the_end_stale_whenever_it_is_claimed() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, "{}");
        var handedAt = File.GetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, SessionId));
        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, SessionId), handedAt.AddMinutes(-2));

        MuseSessionEndRequest.MarkResumed(paths, SessionId);
        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")!;

        await Assert.That(claimed.HandedAt.UtcDateTime).IsEqualTo(handedAt.AddMinutes(-2));
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt)).IsTrue();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, claimed.HandedAt.AddMinutes(5))).IsFalse();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, "0000000000000000000000000000beef", claimed.HandedAt)).IsFalse();
    }
}
