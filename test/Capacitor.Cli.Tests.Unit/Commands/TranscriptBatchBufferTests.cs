using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class TranscriptBatchBufferTests {
    [Test]
    public async Task SplittingUsesUtf8SizeAndPreservesSourceCoordinates() {
        var line = new string('\u4e00', 600_000);
        var source = new TranscriptBatch {
            SessionId = "parent", AgentId = "child", Vendor = "codex", Strict = true,
            Lines = [line, line, line], LineNumbers = [2, 4, 7]
        };

        var batches = TranscriptBatchBuffer.Split(source).ToArray();

        await Assert.That(batches.Length).IsEqualTo(2);
        await Assert.That(batches[0].LineNumbers!).IsEquivalentTo(new[] { 2, 4 });
        await Assert.That(batches[1].LineNumbers!).IsEquivalentTo(new[] { 7 });
        await Assert.That(batches.All(x => x is { SessionId: "parent", AgentId: "child", Vendor: "codex", Strict: true })).IsTrue();
    }

    [Test]
    public async Task EmptyEnvelopeCanStillCarryMetadata() {
        var batch = new TranscriptBatch { SessionId = "parent", Lines = [], LineNumbers = [] };
        await Assert.That(TranscriptBatchBuffer.Split(batch).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Fills_at_the_line_cap() {
        var buffer = new TranscriptBatchBuffer();

        for (var i = 0; i < TranscriptBatchBuffer.MaxLines - 1; i++) buffer.Add("x", i, 1);

        await Assert.That(buffer.IsFull).IsFalse();

        buffer.Add("x", TranscriptBatchBuffer.MaxLines - 1, 1);

        await Assert.That(buffer.IsFull).IsTrue();
        await Assert.That(buffer.FirstLineNumber).IsEqualTo(0);
        await Assert.That(buffer.LastLineNumber).IsEqualTo(TranscriptBatchBuffer.MaxLines - 1);
    }

    [Test]
    public async Task Refuses_a_line_that_would_pass_the_byte_budget() {
        var buffer = new TranscriptBatchBuffer();
        buffer.Add("big", 0, 3 * 1024 * 1024);

        await Assert.That(buffer.Fits(1024 * 1024)).IsTrue();
        await Assert.That(buffer.Fits(1024 * 1024 + 1)).IsFalse();

        buffer.Clear();

        await Assert.That(buffer.IsEmpty).IsTrue();
        await Assert.That(buffer.Fits(TranscriptBatchBuffer.MaxBytes)).IsTrue();
    }

    [Test]
    public async Task Measures_a_line_in_utf8_bytes() {
        await Assert.That(TranscriptBatchBuffer.SizeOf("x")).IsEqualTo(1);
        await Assert.That(TranscriptBatchBuffer.SizeOf("é")).IsEqualTo(2);
        await Assert.That(TranscriptBatchBuffer.SizeOf("日")).IsEqualTo(3);
    }
}
