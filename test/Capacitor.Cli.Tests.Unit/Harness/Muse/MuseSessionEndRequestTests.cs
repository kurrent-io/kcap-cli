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

        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher", TimeProvider.System);
        await Assert.That(claimed).IsNotNull();
        await Assert.That(claimed!.Payload).IsEqualTo("""{"session_id":"x","reason":"exit"}""");
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();

        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "hook", TimeProvider.System)).IsNull();

        MuseSessionEndRequest.Release(claimed.ClaimedPath);
        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
    }

    [Test]
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher", TimeProvider.System)).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher", TimeProvider.System)).IsNull();
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }

    [Test]
    public async Task A_resume_marked_after_the_claim_is_seen_and_one_before_it_is_not() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.MarkResumed(paths, SessionId);
        var later = DateTimeOffset.UtcNow.AddMinutes(1);
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, later)).IsFalse();

        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, SessionId, earlier)).IsTrue();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, "0000000000000000000000000000beef", earlier)).IsFalse();
    }
}
