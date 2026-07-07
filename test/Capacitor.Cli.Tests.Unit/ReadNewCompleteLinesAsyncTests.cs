using System.Text;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

// AI-1243 / Qodo #291: the streaming drain reader (ReadNewCompleteLinesAsync) replaces
// File.ReadAllTextAsync in DrainNewLines. It must (1) open with FileShare.ReadWrite so a
// concurrently-writing agent is never blocked (#291 #1), (2) NOT materialize the whole file
// (#291 #2), and (3) stay byte-for-byte behaviour-equivalent to the string helper
// (SplitNewCompleteLines) — same partial-final-line hold-back, so a mid-write final line is
// never consumed and dropped. The length-cap makes that hold-back robust against a concurrent
// append happening after the length was sampled.
public class ReadNewCompleteLinesAsyncTests {
    static string WriteTemp(string content) {
        var path = Path.Combine(Path.GetTempPath(), $"kcap-drain-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    static async Task<WatchCommand.NewTranscriptLines> ReadViaStream(
            string path, int linesProcessed, bool holdIncompleteFinalLine) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await WatchCommand.ReadNewCompleteLinesAsync(stream, linesProcessed, holdIncompleteFinalLine, default);
    }

    static async Task AssertParity(string content, int linesProcessed, bool holdIncompleteFinalLine) {
        var expected = WatchCommand.SplitNewCompleteLines(content, linesProcessed, holdIncompleteFinalLine);

        var path = WriteTemp(content);
        try {
            var actual = await ReadViaStream(path, linesProcessed, holdIncompleteFinalLine);

            await Assert.That(actual.Lines).IsEquivalentTo(expected.Lines);
            await Assert.That(actual.LineNumbers).IsEquivalentTo(expected.LineNumbers);
            await Assert.That(actual.NextPosition).IsEqualTo(expected.NextPosition);
        } finally {
            File.Delete(path);
        }
    }

    // ---- 1. Parity with the string helper across the interesting shapes ----

    [Test]
    public async Task Parity_all_lines_when_file_ends_with_newline() =>
        await AssertParity("a\nb\nc\n", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_partial_final_line_held_back_when_no_trailing_newline() =>
        await AssertParity("a\nb\nc", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_blank_lines_skipped_but_counted() =>
        await AssertParity("a\n\nb\n", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_blank_partial_final_line() =>
        await AssertParity("real\n   ", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_respects_lines_already_processed() =>
        await AssertParity("a\nb\nc\n", 2, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_crlf_terminated_lines() =>
        await AssertParity("a\r\nb\r\n", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_lone_partial_line_holds_at_start() =>
        await AssertParity("partial-json-being-written", 0, holdIncompleteFinalLine: true);

    [Test]
    public async Task Parity_empty_file() =>
        await AssertParity("", 0, holdIncompleteFinalLine: true);

    // ---- 2. Final drain delivers the unterminated final line ----

    [Test]
    public async Task Final_drain_delivers_unterminated_final_line() {
        var path = WriteTemp("a\nb\nc");
        try {
            var r = await ReadViaStream(path, 0, holdIncompleteFinalLine: false);

            await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b", "c" });
            await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 1, 2 });
            await Assert.That(r.NextPosition).IsEqualTo(3);
        } finally {
            File.Delete(path);
        }
    }

    // ---- 3. FileShare.ReadWrite doesn't block a concurrent writer (#291 #1) ----

    [Test]
    public async Task Read_does_not_block_a_concurrent_append_writer() {
        var path = WriteTemp("a\nb\n");
        try {
            // Hold the file open for reading exactly the way DrainNewLines does.
            await using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // A concurrent agent appending to the same file must succeed (no sharing violation).
            // With default FileShare.Read this would throw IOException — the #291 #1 regression.
            await using (var writeStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await using (var writer = new StreamWriter(writeStream)) {
                await writer.WriteLineAsync("c");
            }

            // The read still works while the handle is shared; the append landed before the read,
            // so all three complete lines are surfaced.
            var r = await WatchCommand.ReadNewCompleteLinesAsync(readStream, 0, holdIncompleteFinalLine: true, default);
            await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b", "c" });
        } finally {
            File.Delete(path);
        }
    }

    // ---- 4. Cap correctness: reads stop at the sampled length ----

    [Test]
    public async Task Cap_stops_reading_at_the_sampled_length() {
        // The wrapper mirrors a length sampled BEFORE a concurrent append: bytes past `limit`
        // exist in `inner` but must never be surfaced, so an as-yet unterminated final line can't
        // be made to look complete and get consumed (the AI-1243 bug this PR fixes).
        var full  = Encoding.UTF8.GetBytes("a\nb\nc\n");   // 6 bytes
        var limit = 4L;                                     // "a\nb\n" only
        using var inner = new MemoryStream(full);

        var capped = new WatchCommand.LengthLimitedReadStream(inner, limit);

        var buffer = new byte[full.Length];
        var total  = 0;
        int n;
        while ((n = await capped.ReadAsync(buffer.AsMemory(total))) > 0) {
            total += n;
        }

        await Assert.That(total).IsEqualTo((int)limit);
        await Assert.That(Encoding.UTF8.GetString(buffer, 0, total)).IsEqualTo("a\nb\n");
    }

    [Test]
    public async Task Cap_appended_bytes_are_not_read_this_pass() {
        // End-to-end feel: sample the length while the file is "a\nb\n", append "c\n" AFTER
        // sampling, then read. The appended line must not appear this pass (it's re-read next pass).
        var path = WriteTemp("a\nb\n");
        try {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sampledLength = stream.Length;

            await using (var writeStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await using (var writer = new StreamWriter(writeStream)) {
                await writer.WriteLineAsync("c");
            }

            // Read only up to the sampled length, exactly as ReadNewCompleteLinesAsync does internally.
            using var reader = new StreamReader(new WatchCommand.LengthLimitedReadStream(stream, sampledLength), leaveOpen: true);
            var text = await reader.ReadToEndAsync();

            await Assert.That(text).IsEqualTo("a\nb\n");
            await Assert.That(text).DoesNotContain("c");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Cap_sync_read_stops_at_the_sampled_length() {
        var full  = Encoding.UTF8.GetBytes("hello-world");
        var limit = 5L;
        using var inner = new MemoryStream(full);

        var capped = new WatchCommand.LengthLimitedReadStream(inner, limit);

        var buffer = new byte[full.Length];
        var total  = 0;
        int n;
        while ((n = capped.Read(buffer, total, buffer.Length - total)) > 0) {
            total += n;
        }

        await Assert.That(total).IsEqualTo((int)limit);
        await Assert.That(Encoding.UTF8.GetString(buffer, 0, total)).IsEqualTo("hello");
    }
}
