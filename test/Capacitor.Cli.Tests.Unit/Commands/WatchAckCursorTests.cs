using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The count the Codex hook bounds its declared turns by: published by the root watcher alone, and
/// gone when it stops, so a subagent's watcher sharing the session id never moves or removes it.</summary>
public class WatchAckCursorTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(),
        new FixedCredentialSource(), TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()),
        new GitProviderRouter(), _time, TestAccounts.None, Home);

    WatchAckCursor Cursor => new(Config.Root);

    static readonly string Sid = Guid.NewGuid().ToString("N");

    /// Only an absent file is missing: a reader that waits on the count polls again through anything else.
    [Test]
    public async Task A_count_round_trips_and_anything_else_reads_as_none() {
        await Assert.That(Cursor.TryRead(Sid, out var neverWritten)).IsNull();
        await Assert.That(neverWritten).IsTrue();

        Cursor.Write(Sid, 12);
        await Assert.That(Cursor.TryRead(Sid, out var written)).IsEqualTo(12);
        await Assert.That(written).IsFalse();

        await File.WriteAllTextAsync(Cursor.Path(Sid), "twelve");
        await Assert.That(Cursor.TryRead(Sid, out var garbled)).IsNull();
        await Assert.That(garbled).IsFalse();

        Cursor.Delete(Sid);
        await Assert.That(Cursor.TryRead(Sid, out var deleted)).IsNull();
        await Assert.That(deleted).IsTrue();
    }

    /// Blank lines advance the count with nothing to send, which is the one drain that needs no server.
    [Test]
    public async Task A_root_watcher_publishes_its_count_and_a_child_never_does() {
        var path = Tmp.CreateFile("rollout.jsonl", "\n\n\n");
        await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

        var child = new WatchState { LinesProcessed = 0, ThresholdReached = true };
        await Watch.GatedDrainNewLinesAsync(null, hub, Sid, path, "agent-1", child, "codex", CancellationToken.None);
        await Assert.That(Cursor.TryRead(Sid)).IsNull();

        var root = new WatchState { LinesProcessed = 0, ThresholdReached = true };
        await Watch.GatedDrainNewLinesAsync(null, hub, Sid, path, null, root, "codex", CancellationToken.None);
        await Assert.That(Cursor.TryRead(Sid)).IsEqualTo(3);
    }

    [Test]
    public async Task A_rewind_is_published_too() {
        Watch.PublishAckedLines(Sid, null, "codex", new WatchState { LinesProcessed = 9 });
        Watch.PublishAckedLines(Sid, null, "codex", new WatchState { LinesProcessed = 4 });

        await Assert.That(Cursor.TryRead(Sid)).IsEqualTo(4);
    }

    [Test]
    public async Task Only_the_root_watcher_retires_the_count() {
        Watch.PublishAckedLines(Sid, null, "codex", new WatchState { LinesProcessed = 5 });

        Watch.RetireAckCursor(Sid, "agent-1", "codex");
        await Assert.That(Cursor.TryRead(Sid)).IsEqualTo(5);

        Watch.RetireAckCursor(Sid, null, "codex");
        await Assert.That(Cursor.TryRead(Sid)).IsNull();
    }

    [Test]
    public async Task Another_vendors_watcher_publishes_nothing() {
        Watch.PublishAckedLines(Sid, null, "claude", new WatchState { LinesProcessed = 5 });

        await Assert.That(Cursor.TryRead(Sid)).IsNull();
    }
}
