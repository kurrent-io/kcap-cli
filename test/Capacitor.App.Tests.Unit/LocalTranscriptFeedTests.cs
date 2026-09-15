using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The file feed: appended lines project in order with their byte offsets, a truncated file
/// resets, and a missing file says so.
public class LocalTranscriptFeedTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";

    [Test]
    public async Task Appended_lines_project_with_their_offsets_and_a_missing_file_reads_as_missing() {
        var path = Tmp.PathTo("t.jsonl");
        var logged = new List<string>();
        using var feed = new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", new FakeTimeProvider(), logged.Add);

        await Assert.That(feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        await Assert.That(feed.CurrentOffset).IsNull();

        await File.WriteAllTextAsync(path, UserLine + "\n");
        var first = feed.ReadAppended();
        await Assert.That(first.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(first.Lines.Single().Offset).IsEqualTo(0);
        await Assert.That(first.Lines.Single().Projection.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
        await Assert.That(feed.CurrentOffset).IsEqualTo(UserLine.Length + 1);

        await File.AppendAllTextAsync(path, AssistantLine + "\n");
        var second = feed.ReadAppended();
        await Assert.That(second.Lines.Single().Offset).IsEqualTo(UserLine.Length + 1);
        await Assert.That(second.Lines.Single().Projection.Envelopes[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(logged).IsEmpty();
    }

    [Test]
    public async Task A_truncated_file_resets_and_reprojects_from_its_start() {
        var path = Tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, UserLine + "\n" + AssistantLine + "\n");
        using var feed = new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", new FakeTimeProvider(), _ => { });
        await Assert.That(feed.ReadAppended().Lines.Count).IsEqualTo(2);

        await File.WriteAllTextAsync(path, UserLine + "\n");
        var reset = feed.ReadAppended();
        await Assert.That(reset.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(reset.Lines.Single().Offset).IsEqualTo(0);
        await Assert.That(reset.SnapshotOffset).IsEqualTo(UserLine.Length + 1);
    }
}
