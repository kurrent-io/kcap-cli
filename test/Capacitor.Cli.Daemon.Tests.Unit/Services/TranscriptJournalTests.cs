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
}
