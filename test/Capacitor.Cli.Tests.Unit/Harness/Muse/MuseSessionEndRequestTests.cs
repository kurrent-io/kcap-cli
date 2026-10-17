using Capacitor.Cli.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Harness.Muse;

public class MuseSessionEndRequestTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "01a13694b0ad7ae28bf52af6357a0847";

    [Test]
    public async Task A_written_request_reads_back_and_is_gone_once_taken() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));

        MuseSessionEndRequest.Write(paths, SessionId, """{"session_id":"x","reason":"exit"}""");

        var read = MuseSessionEndRequest.TryRead(paths, SessionId);
        await Assert.That(read).IsNotNull();
        await Assert.That(read!.Payload).IsEqualTo("""{"session_id":"x","reason":"exit"}""");

        MuseSessionEndRequest.Delete(paths, SessionId);
        await Assert.That(MuseSessionEndRequest.TryRead(paths, SessionId)).IsNull();
    }

    [Test]
    public async Task No_file_or_an_empty_one_is_no_request() {
        var paths = new WatcherPaths(Tmp.PathTo("watchers"));
        await Assert.That(MuseSessionEndRequest.TryRead(paths, SessionId)).IsNull();

        Tmp.CreateFile(["watchers", $"{SessionId}.muse-end-requested"], "");
        await Assert.That(MuseSessionEndRequest.TryRead(paths, SessionId)).IsNull();
    }
}
