using Capacitor.Cli.Core.Harness.Muse;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseHostedSessionMarkerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Dashed   = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
    const string Dashless = "01a13694b0ad7ae28bf52af6357a0847";

    static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    static MuseHostedSessionMarker MarkerWrittenAt(DateTimeOffset at, string session = Dashed) =>
        new(session, "agent-1", "daemon-1", "epoch-1", at);

    [Test]
    public async Task A_written_marker_reads_back_under_either_spelling_of_the_session_id() {
        var marker = MarkerWrittenAt(Now);

        MuseHostedSessionMarker.Write(Config.Root, marker);

        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, Dashed)).IsEqualTo(marker);
        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, Dashless)).IsEqualTo(marker);
        await Assert.That(MuseHostedSessionMarker.PathFor(Config.Root, Dashed))
            .IsEqualTo(MuseHostedSessionMarker.PathFor(Config.Root, Dashless));
        await Assert.That(MuseHostedSessionMarker.PathFor(Config.Root, Dashed))
            .IsEqualTo(Config.Root.Path("muse-hosted", Dashless + ".json"));
    }

    [Test]
    public async Task A_missing_marker_reads_as_null() =>
        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, Dashed)).IsNull();

    [Test]
    public async Task A_malformed_marker_reads_as_null() {
        var path = MuseHostedSessionMarker.PathFor(Config.Root, Dashed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{not json");

        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, Dashed)).IsNull();
    }

    [Test]
    public async Task A_session_id_that_is_not_a_uuid_reads_as_null() =>
        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, "../escape")).IsNull();

    [Test]
    public async Task Delete_removes_the_marker_and_is_idempotent() {
        MuseHostedSessionMarker.Write(Config.Root, MarkerWrittenAt(Now));

        MuseHostedSessionMarker.Delete(Config.Root, Dashless);
        MuseHostedSessionMarker.Delete(Config.Root, Dashless);

        await Assert.That(File.Exists(MuseHostedSessionMarker.PathFor(Config.Root, Dashed))).IsFalse();
    }

    [Test]
    public async Task Sweep_removes_a_marker_older_than_the_window_and_keeps_a_fresh_one() {
        const string other = "01a13694-b0ad-7ae2-8bf5-2af6357a0848";
        var time = new FakeTimeProvider(Now);
        MuseHostedSessionMarker.Write(Config.Root, MarkerWrittenAt(Now - TimeSpan.FromHours(25)));
        MuseHostedSessionMarker.Write(Config.Root, MarkerWrittenAt(Now - TimeSpan.FromHours(1), other));

        MuseHostedSessionMarker.Sweep(Config.Root, time, TimeSpan.FromHours(24));

        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, Dashed)).IsNull();
        await Assert.That(MuseHostedSessionMarker.TryRead(Config.Root, other)).IsNotNull();
    }

    [Test]
    public async Task Sweep_without_a_marker_directory_does_nothing() {
        MuseHostedSessionMarker.Sweep(Config.Root, new FakeTimeProvider(Now), TimeSpan.FromHours(24));

        await Assert.That(Directory.Exists(Config.Root.Path("muse-hosted"))).IsFalse();
    }
}
