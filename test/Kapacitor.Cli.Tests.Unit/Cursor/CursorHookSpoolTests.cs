using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHookSpoolTests {
    const string Sid = "8c3276c2c8f743ce98898c2becf5240a";

    [Test]
    public async Task Append_creates_file_and_writes_one_line_per_call() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);

        spool.Append(Sid, "sessionStart", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");
        spool.Append(Sid, "sessionEnd", $$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, Sid + ".jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("sessionStart");
        await Assert.That(lines[1]).Contains("sessionEnd");
    }

    [Test]
    public async Task Drain_yields_entries_in_FIFO_order() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);
        spool.Append(Sid, "sessionStart", """{"k":"a"}""");
        spool.Append(Sid, "sessionEnd", """{"k":"b"}""");

        var seen = new List<(string Event, string Body)>();

        await foreach (var entry in spool.DrainAsync(Sid, CancellationToken.None)) {
            seen.Add((entry.EventName, entry.Body));
            await entry.MarkDeliveredAsync();
        }

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Event).IsEqualTo("sessionStart");
        await Assert.That(seen[1].Event).IsEqualTo("sessionEnd");
        await Assert.That(File.Exists(Path.Combine(tmp.Path, Sid + ".jsonl"))).IsFalse();
    }

    [Test]
    public async Task Drain_stops_on_first_undelivered_and_preserves_remaining() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);
        spool.Append(Sid, "sessionStart", """{"k":"a"}""");
        spool.Append(Sid, "sessionEnd", """{"k":"b"}""");

        await foreach (var entry in spool.DrainAsync(Sid, CancellationToken.None)) {
            if (entry.EventName == "sessionStart") {
                await entry.MarkDeliveredAsync();
            } else {
                break;
            }
        }

        var remaining = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, Sid + ".jsonl"));
        await Assert.That(remaining.Length).IsEqualTo(1);
        await Assert.That(remaining[0]).Contains("sessionEnd");
    }

    [Test]
    public async Task Append_evicts_oldest_when_over_one_MB() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path, capBytes: 4_096);
        var       big   = new string('x', 1_500);
        spool.Append(Sid, "afterAgentThought", $"\"{big}-first\"");
        spool.Append(Sid, "afterAgentThought", $"\"{big}-second\"");
        spool.Append(Sid, "afterAgentThought", $"\"{big}-third\"");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, Sid + ".jsonl"));
        await Assert.That(lines.Length).IsLessThanOrEqualTo(3);
        await Assert.That(lines.Any(l => l.Contains("-first"))).IsFalse();
        await Assert.That(lines.Last()).Contains("-third");
    }

    [Test]
    public async Task DeleteSession_removes_file_and_is_idempotent() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);
        spool.Append(Sid, "sessionEnd", """{"k":"x"}""");

        spool.DeleteSession(Sid);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, Sid + ".jsonl"))).IsFalse();
        spool.DeleteSession(Sid);
    }

    [Test]
    public async Task ReapOlderThan_deletes_old_files_only() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);
        spool.Append("aaaabbbbccccddddeeeeffffaaaabbbb", "sessionEnd", """{"k":"o"}""");
        spool.Append("bbbbccccddddeeeeffffaaaabbbbcccc", "sessionEnd", """{"k":"n"}""");

        var oldFile = Path.Combine(tmp.Path, "aaaabbbbccccddddeeeeffffaaaabbbb.jsonl");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31));

        spool.ReapOlderThan(TimeSpan.FromDays(30));

        await Assert.That(File.Exists(oldFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "bbbbccccddddeeeeffffaaaabbbbcccc.jsonl"))).IsTrue();
    }

    [Test]
    public async Task unsafe_session_id_is_rejected_silently() {
        using var tmp   = new TempDir();
        var       spool = new CursorHookSpool(tmp.Path);

        spool.Append("../escape", "sessionStart", """{"k":"v"}""");
        spool.Append("/etc/passwd", "sessionStart", """{"k":"v"}""");
        spool.Append("not-32-hex", "sessionStart", """{"k":"v"}""");

        // No files written anywhere — directory empty.
        var files = Directory.Exists(tmp.Path)
            ? Directory.EnumerateFiles(tmp.Path, "*", SearchOption.AllDirectories).ToList()
            : [];
        await Assert.That(files).IsEmpty();

        // Drain on the same unsafe IDs yields nothing.
        await foreach (var _ in spool.DrainAsync("../escape", CancellationToken.None)) {
            throw new Exception("Should not yield for unsafe session id");
        }

        spool.DeleteSession("../escape"); // must not throw
    }

    [Test]
    public async Task Drain_stops_on_malformed_line_and_preserves_remainder() {
        using var tmp       = new TempDir();
        var       spool     = new CursorHookSpool(tmp.Path);
        var       spoolFile = Path.Combine(tmp.Path, Sid + ".jsonl");

        // Simulate a partial concurrent write: a malformed middle line.
        Directory.CreateDirectory(tmp.Path);

        File.WriteAllLines(
            spoolFile,
            [
                """{"hook_event_name":"sessionStart","body":"{}"}""",
                "{partial-write-here", // malformed
                """{"hook_event_name":"sessionEnd","body":"{}"}"""
            ]
        );

        var seen = new List<string>();

        await foreach (var entry in spool.DrainAsync(Sid, CancellationToken.None)) {
            seen.Add(entry.EventName);
            await entry.MarkDeliveredAsync();
        }

        // The first entry should have been delivered cleanly. The malformed
        // line and everything after it MUST remain so the next invocation
        // can retry.
        await Assert.That(seen).IsEquivalentTo(["sessionStart"]);

        var remaining = await File.ReadAllLinesAsync(spoolFile);
        await Assert.That(remaining.Length).IsEqualTo(2);
        await Assert.That(remaining[0]).IsEqualTo("{partial-write-here");
        await Assert.That(remaining[1]).Contains("sessionEnd");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-spool-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
