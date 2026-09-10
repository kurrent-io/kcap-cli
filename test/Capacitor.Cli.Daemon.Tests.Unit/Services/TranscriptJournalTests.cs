using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TranscriptJournalTests {
    static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero));

    static string[] Lines(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    static AcpEventEnvelope Text(string t) => new(Kind: AcpEventKind.AssistantText, Text: t);

    [Test]
    public async Task Open_writes_the_header_synchronously_and_reports_created() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);

        await Assert.That(journal.Open("/w", "m1")).IsTrue();

        await Assert.That(journal.IsOpen).IsTrue();
        await Assert.That(journal.CreatedFile).IsTrue();
        await Assert.That(journal.Path).IsEqualTo(Path.Combine(tmp.Path, "transcripts", AgentFileNames.For("agent-1") + ".jsonl"));
        var lines = Lines(journal.Path);
        await Assert.That(lines).Count().IsEqualTo(1);
        await Assert.That(EnvelopeJournalFormat.TryRead(lines[0], out var header)).IsTrue();
        await Assert.That(header.Kind).IsEqualTo(AcpEventKind.SessionStarted);
        await Assert.That(header.Cwd).IsEqualTo("/w");
        await Assert.That(header.Model).IsEqualTo("m1");
        await Assert.That(header.RawSessionId).IsNull();
        await journal.CompleteAsync();
    }

    [Test]
    public async Task Second_open_of_the_same_agent_appends_a_header_and_keeps_every_byte() {
        using var tmp = new TempDir();
        var first = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        first.Open("/w", null);
        first.Record(Text("a"));
        await Assert.That(await first.CompleteAsync()).IsTrue();
        var before = Lines(first.Path);

        var second = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        second.Open("/w", null);
        await Assert.That(second.CreatedFile).IsFalse();
        await second.CompleteAsync();

        var after = Lines(second.Path);
        await Assert.That(after.Take(before.Length)).IsEquivalentTo(before);
        await Assert.That(after).Count().IsEqualTo(before.Length + 1);
    }

    [Test]
    public async Task Record_appends_in_order_skips_ephemerals_and_drains_on_complete() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        journal.Record(Text("a"));
        journal.Record(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "live", Ephemeral: true));
        journal.Record(Text("b"));

        await Assert.That(await journal.CompleteAsync()).IsTrue();

        var texts = Lines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return e.Text; });
        await Assert.That(texts).IsEquivalentTo(new string?[] { "a", "b" });
    }

    [Test]
    public async Task No_handle_is_held_between_writes() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        journal.Record(Text("a"));
        await journal.CompleteAsync();

        // Exclusive open and delete both succeed: nothing else holds the file.
        using (new FileStream(journal.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(journal.Path);
        await Assert.That(File.Exists(journal.Path)).IsFalse();
    }

    [Test]
    public async Task Header_failure_leaves_the_journal_closed_and_record_a_noop() {
        using var tmp = new TempDir();
        var blockedDir = tmp.CreateFile("transcripts"); // a FILE where the directory should be
        var journal = new TranscriptJournal(Path.Combine(blockedDir, "x.jsonl"), NullLogger.Instance);

        await Assert.That(journal.Open(null, null)).IsFalse();

        await Assert.That(journal.IsOpen).IsFalse();
        journal.Record(Text("a")); // must not throw
        await Assert.That(await journal.CompleteAsync()).IsTrue();
    }

    [Test]
    public async Task Record_never_blocks_while_the_sink_hangs() {
        using var tmp = new TempDir();
        using var hang = new ManualResetEventSlim(false);
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time,
            append: (path, bytes) => { if (Lines(path).Length >= 1) hang.Wait(); File.AppendAllText(path, System.Text.Encoding.UTF8.GetString(bytes)); },
            capacity: 4);
        journal.Open(null, null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) journal.Record(Text(i.ToString(CultureInfo.InvariantCulture)));
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(500));

        await Assert.That(journal.PendingGap).IsGreaterThan(0);
        hang.Set();
        await journal.CompleteAsync();
    }

    /// A sink that blocks every append until released, then writes for real.
    sealed class GatedSink : IDisposable {
        public readonly SemaphoreSlim Release = new(0);
        public int Appends;

        public void Append(string path, byte[] bytes) {
            Release.Wait();
            Interlocked.Increment(ref Appends);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(0, SeekOrigin.End);
            fs.Write(bytes);
            fs.Flush(true);
        }

        public void Dispose() => Release.Dispose();
    }

    /// Held until a test has enqueued everything it means to: the writer's first read is what the
    /// gap assertions race, and completing a TaskCompletionSource inline would run that read here.
    static TaskCompletionSource WriterGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task Gap_note_lands_between_the_last_kept_and_the_first_after_the_loss() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var gate = WriterGate();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 2, writerStartGate: gate.Task);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b")); // fill the queue (the writer has not read yet)
        journal.Record(Text("lost-1")); journal.Record(Text("lost-2"));
        await Assert.That(journal.PendingGap).IsEqualTo(2);
        gate.SetResult();
        sink.Release.Release(); // the writer takes "a": one slot frees
        await Task.Delay(100);
        journal.Record(Text("c")); // exactly one slot free: this item carries the gap
        await Assert.That(journal.PendingGap).IsEqualTo(0);
        sink.Release.Release(10);
        await Assert.That(await journal.CompleteAsync()).IsTrue();

        var texts = Lines(journal.Path).Skip(1).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return (e.Kind, e.Text); }).ToList();
        await Assert.That(texts).IsEquivalentTo(new (string, string?)[] {
            (AcpEventKind.AssistantText, "a"), (AcpEventKind.AssistantText, "b"),
            (AcpEventKind.SystemNote, "2 envelopes were not recorded to this journal"), (AcpEventKind.AssistantText, "c") });
    }

    [Test]
    public async Task Gap_pending_at_completion_is_written_last() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var gate = WriterGate();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 1, writerStartGate: gate.Task);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost"));
        gate.SetResult();
        sink.Release.Release(10);
        await Assert.That(await journal.CompleteAsync()).IsTrue();
        var last = Lines(journal.Path).Last();
        EnvelopeJournalFormat.TryRead(last, out var e);
        await Assert.That(e.Text).IsEqualTo("1 envelopes were not recorded to this journal");
    }

    [Test]
    public async Task Complete_against_a_hung_sink_returns_within_the_grace_and_reports_not_drained() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var log = new CapturingLogger();
        var gate = WriterGate();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), log, Time, sink.Append, capacity: 2, completeGrace: TimeSpan.FromMilliseconds(200), writerStartGate: gate.Task);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b")); journal.Record(Text("lost"));
        gate.SetResult();
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var drained = await journal.CompleteAsync();

        await Assert.That(drained).IsFalse();
        await Assert.That(journal.Drained).IsFalse();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(log.Warnings.Single()).Contains("assistant_text").And.Contains("1 queued").And.Contains("1 unrecorded");
        journal.Record(Text("after")); // latched: silently ignored
        sink.Release.Release(10);
    }

    [Test]
    public async Task Cancellation_never_splits_a_gap_bearing_item() {
        using var tmp = new TempDir();
        var sink = new GatedSink();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100));
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost"));
        await Task.Delay(50);
        journal.Record(Text("c")); // still full: counted
        var complete = journal.CompleteAsync(); // expires while the sink still blocks on "a"
        await Assert.That(await complete).IsFalse();
        sink.Release.Release(10);
        await Task.Delay(200);

        // The abandoned writer finished "a" (one item, whole) and then observed cancellation: no torn note.
        var lines = Lines(journal.Path);
        foreach (var line in lines) await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsTrue();
        await Assert.That(sink.Appends).IsEqualTo(1);
    }

    [Test]
    public async Task First_append_failure_latches_after_one_warning_and_counts_the_queue() {
        using var tmp = new TempDir();
        var log = new CapturingLogger();
        var journal = new TranscriptJournal(tmp.PathTo("j.jsonl"), log, Time, append: (_, _) => throw new IOException("disk gone"));
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("b"));
        await Assert.That(await journal.CompleteAsync()).IsTrue(); // the writer exited (by faulting), so it drained
        await Assert.That(log.Warnings).Count().IsEqualTo(1);
        await Assert.That(log.Warnings[0]).Contains("write failed");
        await Assert.That(Lines(journal.Path)).Count().IsEqualTo(1); // header only
    }

    [Test]
    public async Task Torn_gap_item_renders_the_note_and_drops_the_torn_envelope() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("j.jsonl");
        var gate = WriterGate();
        // The crash model: a buffer cut after its note line.
        var journal = new TranscriptJournal(path, NullLogger.Instance, Time, append: (p, bytes) => {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var cut = text.IndexOf('\n', StringComparison.Ordinal) + 1 + 5;
            File.AppendAllText(p, text[..Math.Min(cut, text.Length)]);
        }, capacity: 1, writerStartGate: gate.Task);
        journal.Open(null, null);
        journal.Record(Text("a")); journal.Record(Text("lost")); // "a" carries no gap
        gate.SetResult();
        await Task.Delay(50);
        journal.Record(Text("c")); // carries gap 1: note + torn "c"
        await journal.CompleteAsync();

        var tail = new JsonlTail(path).ReadAppended();
        await Assert.That(tail.Lines.Count(l => l.Contains("not recorded", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(tail.Lines.Any(l => l.Contains("\"c\"", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Same_id_open_waits_out_an_abandoned_writer_and_appends_after_its_line() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var locks = new JournalPathLocks();
        var sink = new GatedSink();
        var first = new TranscriptJournal(tmp.PathTo("j.jsonl"), NullLogger.Instance, Time, sink.Append, locks, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100), lockBound: TimeSpan.FromMilliseconds(100));
        first.Open(null, null);
        first.Record(Text("late"));
        await Task.Delay(50);
        await Assert.That(await first.CompleteAsync()).IsFalse(); // writer abandoned inside the (gated) append, holding the path lock

        var second = new TranscriptJournal(first.Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(second.Open(null, null)).IsFalse(); // the lock is held by the abandoned call
        await Assert.That(second.IsOpen).IsFalse();

        sink.Release.Release(10);
        await Task.Delay(200);
        var third = new TranscriptJournal(first.Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(third.Open(null, null)).IsTrue();
        await third.CompleteAsync();

        var texts = Lines(first.Path).Select(l => { EnvelopeJournalFormat.TryRead(l, out var e); return e.Kind == AcpEventKind.SessionStarted ? "header" : e.Text; });
        await Assert.That(texts).IsEquivalentTo(new string?[] { "header", "late", "header" });
    }

    [Test]
    public async Task Same_agent_id_under_two_state_dirs_takes_two_locks() {
        using var a = new TempDir(); using var b = new TempDir();
        var locks = new JournalPathLocks();
        var sink = new GatedSink();
        var hung = new TranscriptJournal(TranscriptJournal.ForAgent(a.Path, "agent-1", NullLogger.Instance).Path, NullLogger.Instance, Time, sink.Append, locks, capacity: 1, completeGrace: TimeSpan.FromMilliseconds(100));
        hung.Open(null, null); hung.Record(Text("x"));
        await Task.Delay(50);
        await hung.CompleteAsync();

        var other = new TranscriptJournal(TranscriptJournal.ForAgent(b.Path, "agent-1", NullLogger.Instance).Path, NullLogger.Instance, Time, locks: locks, lockBound: TimeSpan.FromMilliseconds(100));
        await Assert.That(other.Open(null, null)).IsTrue();
        await other.CompleteAsync();
        sink.Release.Release(10);
    }

    [Test]
    public async Task Abrupt_disposal_leaves_exactly_the_lines_the_writer_reached() {
        using var tmp = new TempDir();
        var journal = TranscriptJournal.ForAgent(tmp.Path, "agent-1", NullLogger.Instance);
        journal.Open(null, null);
        for (var i = 0; i < 50; i++) journal.Record(Text(i.ToString(CultureInfo.InvariantCulture)));
        await Task.Delay(300); // no CompleteAsync: the process "dies"
        foreach (var line in Lines(journal.Path)) await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsTrue();
    }
}
