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
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, SessionId, "watcher")).IsNull();
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }

    [Test]
    public async Task Delete_all_removes_the_request_and_every_claim() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, "{}");
        var claimed = MuseSessionEndRequest.TryClaim(paths, SessionId, "w1")!;
        MuseSessionEndRequest.Write(paths, SessionId, "{}");

        MuseSessionEndRequest.DeleteAll(paths, SessionId);

        await Assert.That(File.Exists(claimed.ClaimedPath)).IsFalse();
        await Assert.That(MuseSessionEndRequest.Exists(paths, SessionId)).IsFalse();
    }
}
