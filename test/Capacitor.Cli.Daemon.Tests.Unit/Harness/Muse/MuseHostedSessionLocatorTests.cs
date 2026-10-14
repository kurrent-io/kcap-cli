using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseHostedSessionLocatorTests {
    const string SessionId = "01a13695-0736-75d0-8f4e-f121585b8453";

    static readonly DateOnly Today = new(2026, 10, 13);

    [TempHome] public required TempHome Home { get; init; }

    MusePaths Paths => new(Home, null, null);

    void Mark(string sessionId, string agentId) =>
        MuseHostedSessionMarker.Write(Home, new(sessionId, agentId, "daemon-1", "epoch-1", TimeProvider.System.GetUtcNow()));

    string CreateLog(string sessionId) {
        var path = Paths.SessionLogPath(sessionId, Today)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    (string SessionId, string Path)? Locate(string agentId, MuseSessionDescriptor? opened = null) =>
        MuseHostedSessionLocator.TryLocate(Home, Paths, agentId, Today, opened);

    [Test]
    public async Task Nothing_is_found_without_a_marker() =>
        await Assert.That(Locate("agent-1")).IsNull();

    [Test]
    public async Task Another_agents_marker_is_not_this_agents_session() {
        Mark(SessionId, "agent-2");
        CreateLog(SessionId);

        await Assert.That(Locate("agent-1")).IsNull();
    }

    [Test]
    public async Task A_session_whose_log_does_not_exist_yet_is_not_found() {
        Mark(SessionId, "agent-1");

        await Assert.That(Locate("agent-1")).IsNull();
    }

    [Test]
    public async Task The_marked_session_is_found_once_its_log_exists() {
        Mark(SessionId, "agent-1");
        var log = CreateLog(SessionId);

        var found = Locate("agent-1");

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value.SessionId).IsEqualTo(SessionId.Replace("-", ""));
        await Assert.That(found.Value.Path).IsEqualTo(log);
    }

    [Test]
    public async Task A_file_that_is_not_a_marker_is_skipped() {
        Home.CreateFile(Path.Combine(".config", "kcap", "muse-hosted", "not-a-guid.json"), "{}");
        Mark(SessionId, "agent-1");
        CreateLog(SessionId);

        await Assert.That(Locate("agent-1")?.SessionId).IsEqualTo(SessionId.Replace("-", ""));
    }

    /// <summary>A session that ended before the first poll has lost its marker to the end hook; the
    /// runtime's own session still links it.</summary>
    [Test]
    public async Task The_runtimes_session_is_found_without_a_marker() {
        var log = CreateLog(SessionId);

        var found = Locate("agent-1", new MuseSessionDescriptor(SessionId, log, null, "/workspace"));

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Value.SessionId).IsEqualTo(SessionId.Replace("-", ""));
        await Assert.That(found.Value.Path).IsEqualTo(log);
    }

    [Test]
    public async Task The_runtimes_session_is_not_found_before_its_log_exists() {
        var opened = new MuseSessionDescriptor(SessionId, Paths.SessionLogPath(SessionId, Today)!, null, "/workspace");

        await Assert.That(Locate("agent-1", opened)).IsNull();
    }
}
