using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>One read of the lines before a resume cursor feeds every consumer: a full one every line, a
/// windowed one the last lines only, none anything from the cursor on, which the drain delivers.</summary>
public class TranscriptReplayTests {
    static TranscriptReplayConsumer Recording(List<string> seen, bool windowOnly, int stopAfter = int.MaxValue) =>
        new(windowOnly, line => {
            seen.Add(line);
            return seen.Count >= stopAfter;
        });

    [Test]
    public async Task One_read_feeds_each_consumer_its_lines_before_the_cursor() {
        using var tmp = new TempDir();
        var lines = Enumerable.Range(0, TranscriptReplay.WindowLines + 3).Select(i => $"line {i}").ToList();
        var path = tmp.CreateFile("t.jsonl", [.. lines]);
        List<string> full = [], window = [], stopped = [];

        await TranscriptReplay.ReplayAsync(path, upToLine: lines.Count - 1,
            [Recording(full, windowOnly: false), Recording(window, windowOnly: true), Recording(stopped, windowOnly: false, stopAfter: 2)],
            CancellationToken.None, _ => { });

        await Assert.That(full).IsEquivalentTo(lines.Take(lines.Count - 1));
        await Assert.That(window).IsEquivalentTo(lines.Skip(2).Take(TranscriptReplay.WindowLines));
        await Assert.That(stopped).IsEquivalentTo(lines.Take(2));
    }

    [Test]
    public async Task A_missing_transcript_is_skipped_with_a_log_line() {
        using var tmp = new TempDir();
        List<string> seen = [], logged = [];

        await TranscriptReplay.ReplayAsync(tmp.PathTo("missing.jsonl"), 5, [Recording(seen, windowOnly: false)], CancellationToken.None, logged.Add);

        await Assert.That(seen).IsEmpty();
        await Assert.That(logged).HasSingleItem();
        await Assert.That(logged[0]).StartsWith("Transcript replay before the resume cursor skipped");
    }

    [Test]
    public async Task A_cancelled_start_reads_nothing_and_logs_nothing() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("t.jsonl", ["line 0"]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        List<string> seen = [], logged = [];

        await TranscriptReplay.ReplayAsync(path, 1, [Recording(seen, windowOnly: false)], cancelled.Token, logged.Add);

        await Assert.That(seen).IsEmpty();
        await Assert.That(logged).IsEmpty();
    }

    /// A checkout the agent mutated before a restart is seeded from the lines the resumed drain skips.
    [Test]
    public async Task Secondary_roots_are_seeded_from_mutations_before_the_cursor() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("t.jsonl", [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/h/dev/cli-wt/a.cs"}}]}}""",
            "{not json",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"file_path":"/h/dev/later/b.cs"}}]}}""",
        ]);
        var roots = new SecondaryRepoRoots(dir => dir.StartsWith("/h/dev/", StringComparison.Ordinal) ? string.Join('/', dir.Split('/').Take(4)) : null, primaryRoot: "/h/dev/main");

        await TranscriptReplay.ReplayAsync(path, upToLine: 2, [WatchCommand.SecondaryRootsReplay(roots, "claude")], CancellationToken.None, _ => { });

        await Assert.That(roots.Roots).IsEquivalentTo(["/h/dev/cli-wt"]);
    }
}
