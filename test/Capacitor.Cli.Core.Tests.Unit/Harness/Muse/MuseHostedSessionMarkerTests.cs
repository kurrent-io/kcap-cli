using System.Runtime.Versioning;
using Capacitor.Cli.Core.Harness.Muse;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseHostedSessionMarkerTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Dashed   = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
    const string Dashless = "01a13694b0ad7ae28bf52af6357a0847";

    static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    static MuseHostedSessionMarker MarkerWrittenAt(DateTimeOffset at, string session = Dashed) =>
        new(session, "agent-1", "daemon-1", "epoch-1", at);

    [Test]
    public async Task A_written_marker_reads_back_under_either_spelling_of_the_session_id() {
        var marker = MarkerWrittenAt(Now);

        MuseHostedSessionMarker.Write(Home, marker);

        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashed)).IsEqualTo(marker);
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashless)).IsEqualTo(marker);
        await Assert.That(MuseHostedSessionMarker.PathFor(Home, Dashed))
            .IsEqualTo(MuseHostedSessionMarker.PathFor(Home, Dashless));
        await Assert.That(MuseHostedSessionMarker.PathFor(Home, Dashed))
            .IsEqualTo(Home.PathTo(".config", "kcap", "muse-hosted", Dashless + ".json"));
    }

    [Test]
    public async Task A_marker_round_trips_the_daemon_connection() {
        var marker = MarkerWrittenAt(Now) with { ConfigDir = "/srv/kcap-config", ServerUrl = "https://kcap.example", Profile = "team-b" };

        MuseHostedSessionMarker.Write(Home, marker);

        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashed)).IsEqualTo(marker);
    }

    /// <summary>A marker written without the connection fields still reads, naming none.</summary>
    [Test]
    public async Task A_marker_without_the_connection_fields_reads_with_none() {
        var path = MuseHostedSessionMarker.PathFor(Home, Dashed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            $$"""{"session_id":"{{Dashed}}","agent_id":"agent-1","daemon_id":"daemon-1","daemon_epoch":"epoch-1","written_at":"2026-10-13T12:00:00+00:00"}""");

        var marker = MuseHostedSessionMarker.TryRead(Home, Dashed);

        await Assert.That(marker).IsEqualTo(MarkerWrittenAt(Now));
        await Assert.That(marker!.ConfigDir).IsNull();
    }

    [Test]
    public async Task A_missing_marker_reads_as_null() =>
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashed)).IsNull();

    [Test]
    public async Task A_malformed_marker_reads_as_null() {
        var path = MuseHostedSessionMarker.PathFor(Home, Dashed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{not json");

        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashed)).IsNull();
    }

    [Test]
    public async Task A_session_id_that_is_not_a_uuid_reads_as_null() =>
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, "../escape")).IsNull();

    [Test]
    public async Task Delete_removes_the_marker_and_is_idempotent() {
        MuseHostedSessionMarker.Write(Home, MarkerWrittenAt(Now));

        MuseHostedSessionMarker.Delete(Home, Dashless);
        MuseHostedSessionMarker.Delete(Home, Dashless);

        await Assert.That(File.Exists(MuseHostedSessionMarker.PathFor(Home, Dashed))).IsFalse();
    }

    [Test]
    public async Task Sweep_removes_a_marker_older_than_the_window_and_keeps_a_fresh_one() {
        const string other = "01a13694-b0ad-7ae2-8bf5-2af6357a0848";
        var time = new FakeTimeProvider(Now);
        MuseHostedSessionMarker.Write(Home, MarkerWrittenAt(Now - TimeSpan.FromHours(25)));
        MuseHostedSessionMarker.Write(Home, MarkerWrittenAt(Now - TimeSpan.FromHours(1), other));

        MuseHostedSessionMarker.Sweep(Home, time, TimeSpan.FromHours(24));

        await Assert.That(MuseHostedSessionMarker.TryRead(Home, Dashed)).IsNull();
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, other)).IsNotNull();
    }

    /// <summary>Several unreadable files are interleaved with the stale markers so that, in whatever
    /// order the directory enumerates, some stale marker follows an unreadable one.</summary>
    [Test, ExcludeOn(TUnit.Core.Enums.OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Sweep_skips_an_unreadable_marker_and_removes_the_rest() {
        var time  = new FakeTimeProvider(Now);
        var stale = Enumerable.Range(0, 8).Select(i => $"01a13694-b0ad-7ae2-8bf5-2af6357a09{i:D2}").ToArray();

        foreach (var id in stale) MuseHostedSessionMarker.Write(Home, MarkerWrittenAt(Now - TimeSpan.FromHours(25), id));

        var unreadable = Enumerable.Range(0, 8).Select(i => Path.Join(MuseHostedSessionMarker.DirectoryFor(Home), $"unreadable-{i}.json")).ToArray();
        foreach (var path in unreadable) {
            await File.WriteAllTextAsync(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        try {
            MuseHostedSessionMarker.Sweep(Home, time, TimeSpan.FromHours(24));

            foreach (var id in stale)
                await Assert.That(File.Exists(MuseHostedSessionMarker.PathFor(Home, id))).IsFalse();
        } finally {
            foreach (var path in unreadable.Where(File.Exists)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Sweep_without_a_marker_directory_does_nothing() {
        MuseHostedSessionMarker.Sweep(Home, new FakeTimeProvider(Now), TimeSpan.FromHours(24));

        await Assert.That(Directory.Exists(MuseHostedSessionMarker.DirectoryFor(Home))).IsFalse();
    }
}
