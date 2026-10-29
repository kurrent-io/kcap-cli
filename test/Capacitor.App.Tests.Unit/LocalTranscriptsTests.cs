using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// A transcript is tailed once for every pane that opens it: a later pane starts from the rows
/// already projected, and a truncation replays to each pane as a reset.
public class LocalTranscriptsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";

    static IChatTranscriptFeed Open(LocalTranscripts transcripts, string path) =>
        transcripts.Open(path, TranscriptChat.For("claude")!, "a1", new FakeTimeProvider(), _ => { });

    [Test]
    public async Task A_later_pane_starts_from_the_projected_rows_and_both_get_appends() {
        var path = Tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, UserLine + "\n");
        var transcripts = new LocalTranscripts();
        using var first = Open(transcripts, path);
        var shown = first.ReadAppended();

        using var second = Open(transcripts, path);
        var replayed = second.ReadAppended();

        await Assert.That(replayed.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(replayed.Lines.Single().Projection).IsSameReferenceAs(shown.Lines.Single().Projection);
        await Assert.That(replayed.SnapshotOffset).IsEqualTo(UserLine.Length + 1);

        await File.AppendAllTextAsync(path, AssistantLine + "\n");
        await Assert.That(second.ReadAppended().Lines.Single().Offset).IsEqualTo(UserLine.Length + 1);
        await Assert.That(first.ReadAppended().Lines.Single().Offset).IsEqualTo(UserLine.Length + 1);
    }

    [Test]
    public async Task A_truncation_replays_to_every_pane_as_a_reset() {
        var path = Tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, UserLine + "\n" + AssistantLine + "\n");
        var transcripts = new LocalTranscripts();
        using var first = Open(transcripts, path);
        using var second = Open(transcripts, path);
        first.ReadAppended();
        second.ReadAppended();

        await File.WriteAllTextAsync(path, UserLine + "\n");

        foreach (var feed in new[] { first, second }) {
            var read = feed.ReadAppended();
            await Assert.That(read.Status).IsEqualTo(FeedStatus.Reset);
            await Assert.That(read.Lines.Single().Offset).IsEqualTo(0);
        }
    }
}
