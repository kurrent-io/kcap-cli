using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorHookSpoolTests {
    [Test]
    public async Task Append_creates_file_and_writes_one_line_per_call() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);

        spool.Append("abc123", "sessionStart", """{"hook_event_name":"sessionStart","session_id":"abc123"}""");
        spool.Append("abc123", "sessionEnd",   """{"hook_event_name":"sessionEnd","session_id":"abc123"}""");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc123.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("sessionStart");
        await Assert.That(lines[1]).Contains("sessionEnd");
    }

    [Test]
    public async Task Drain_yields_entries_in_FIFO_order() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionStart", """{"k":"a"}""");
        spool.Append("abc", "sessionEnd",   """{"k":"b"}""");

        var seen = new List<(string Event, string Body)>();
        await foreach (var entry in spool.DrainAsync("abc", CancellationToken.None)) {
            seen.Add((entry.EventName, entry.Body));
            await entry.MarkDeliveredAsync();
        }

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0].Event).IsEqualTo("sessionStart");
        await Assert.That(seen[1].Event).IsEqualTo("sessionEnd");
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "abc.jsonl"))).IsFalse();
    }

    [Test]
    public async Task Drain_stops_on_first_undelivered_and_preserves_remaining() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionStart", """{"k":"a"}""");
        spool.Append("abc", "sessionEnd",   """{"k":"b"}""");

        await foreach (var entry in spool.DrainAsync("abc", CancellationToken.None)) {
            if (entry.EventName == "sessionStart") {
                await entry.MarkDeliveredAsync();
            } else {
                break;
            }
        }

        var remaining = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc.jsonl"));
        await Assert.That(remaining.Length).IsEqualTo(1);
        await Assert.That(remaining[0]).Contains("sessionEnd");
    }

    [Test]
    public async Task Append_evicts_oldest_when_over_one_MB() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path, capBytes: 4_096);
        var big = new string('x', 1_500);
        spool.Append("abc", "afterAgentThought", $"\"{big}-first\"");
        spool.Append("abc", "afterAgentThought", $"\"{big}-second\"");
        spool.Append("abc", "afterAgentThought", $"\"{big}-third\"");

        var lines = await File.ReadAllLinesAsync(Path.Combine(tmp.Path, "abc.jsonl"));
        await Assert.That(lines.Length).IsLessThanOrEqualTo(3);
        await Assert.That(lines.Any(l => l.Contains("-first"))).IsFalse();
        await Assert.That(lines.Last()).Contains("-third");
    }

    [Test]
    public async Task DeleteSession_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("abc", "sessionEnd", """{"k":"x"}""");

        spool.DeleteSession("abc");
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "abc.jsonl"))).IsFalse();
        spool.DeleteSession("abc");
    }

    [Test]
    public async Task ReapOlderThan_deletes_old_files_only() {
        using var tmp = new TempDir();
        var spool = new CursorHookSpool(tmp.Path);
        spool.Append("oldsession", "sessionEnd", """{"k":"o"}""");
        spool.Append("newsession", "sessionEnd", """{"k":"n"}""");

        var oldFile = Path.Combine(tmp.Path, "oldsession.jsonl");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31));

        spool.ReapOlderThan(TimeSpan.FromDays(30));

        await Assert.That(File.Exists(oldFile)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, "newsession.jsonl"))).IsTrue();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-spool-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
