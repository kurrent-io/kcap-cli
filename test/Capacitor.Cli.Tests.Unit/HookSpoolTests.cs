using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class HookSpoolTests {
    static string TmpDir() =>
        Path.Combine(Path.GetTempPath(), $"kcap-spool-{Guid.NewGuid():N}");

    const string SidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public async Task drains_current_session_first_then_others_in_fifo() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidB, "session-start", """{"n":"b1"}""");
            spool.Append(SidA, "session-start", """{"n":"a1"}""");
            spool.Append(SidA, "session-end",   """{"n":"a2"}""");

            var seen = new List<string>();
            await spool.DrainAllAsync(SidA, (route, body) => {
                seen.Add($"{route}:{body}");
                return Task.FromResult(DrainOutcome.Delivered);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

            // Current session A first (FIFO a1, a2), then B.
            await Assert.That(seen).IsEquivalentTo([
                """session-start:{"n":"a1"}""",
                """session-end:{"n":"a2"}""",
                """session-start:{"n":"b1"}""",
            ]);
            await Assert.That(Directory.EnumerateFiles(dir)).IsEmpty();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task transient_stop_keeps_remainder_drop_advances() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-start", """{"n":1}"""); // Delivered
            spool.Append(SidA, "session-start", """{"n":2}"""); // Drop (permanent)
            spool.Append(SidA, "session-end",   """{"n":3}"""); // TransientStop

            await spool.DrainAllAsync(SidA, (_, body) =>
                Task.FromResult(body.Contains("2") ? DrainOutcome.Drop
                              : body.Contains("3") ? DrainOutcome.TransientStop
                              : DrainOutcome.Delivered),
                TimeSpan.FromSeconds(5), CancellationToken.None);

            // n1 delivered, n2 dropped, n3 left for next time. After a partial drain the
            // remainder lives in a .draining temp; read whatever files remain in the dir.
            var all = string.Concat(Directory.EnumerateFiles(dir).Select(File.ReadAllText));
            // body values may be Unicode-escaped (" for "), so check the body field content directly
            await Assert.That(all).Contains("session-end"); // n3 route still present
            await Assert.That(all).DoesNotContain("session-start"); // n1 (delivered) and n2 (dropped) gone
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task concurrent_append_during_drain_is_not_lost() {
        var dir = TmpDir();
        try {
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-start", """{"n":"old"}""");

            // Poster appends a NEW entry while the OLD one is being drained (live file
            // already rotated to a temp), simulating a racing hook on the same session.
            var appended = false;
            await spool.DrainAllAsync(SidA, (_, _) => {
                if (!appended) { spool.Append(SidA, "session-end", """{"n":"new"}"""); appended = true; }
                return Task.FromResult(DrainOutcome.Delivered);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);

            var all = string.Concat(Directory.EnumerateFiles(dir).Select(File.ReadAllText));
            await Assert.That(all).Contains("new"); // survived in a fresh live file
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task old_format_lines_without_route_are_skipped() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            await File.WriteAllTextAsync(Path.Combine(dir, $"{SidA}.jsonl"),
                "{\"hook_event_name\":\"sessionEnd\",\"body\":\"x\"}\n");
            var count = 0;
            var spool = new HookSpool(dir);
            await spool.DrainAllAsync(SidA, (_, _) => { count++; return Task.FromResult(DrainOutcome.Delivered); },
                TimeSpan.FromSeconds(5), CancellationToken.None);
            await Assert.That(count).IsEqualTo(0); // skipped, not posted
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task recovered_draining_temp_drains_before_live_file() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            // Simulate a crash mid-drain: an older .draining temp + a newer live file.
            await File.WriteAllTextAsync(Path.Combine(dir, $"{SidA}.123-1.draining"),
                "{\"route\":\"session-start\",\"body\":\"old\"}\n");
            await Task.Delay(10);
            var spool = new HookSpool(dir);
            spool.Append(SidA, "session-end", """{"n":"newlive"}""");

            var seen = new List<string>();
            await spool.DrainAllAsync(SidA, (_, body) => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                TimeSpan.FromSeconds(5), CancellationToken.None);

            await Assert.That(seen[0]).IsEqualTo("old"); // temp first
            await Assert.That(seen).Contains("""{"n":"newlive"}""");
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task reap_deletes_stale_files() {
        var dir = TmpDir();
        Directory.CreateDirectory(dir);
        try {
            var f = Path.Combine(dir, $"{SidA}.jsonl");
            await File.WriteAllTextAsync(f, "{\"route\":\"x\",\"body\":\"y\"}\n");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-40));
            new HookSpool(dir).ReapOlderThan(TimeSpan.FromDays(30));
            await Assert.That(File.Exists(f)).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task cap_holds_in_bytes_for_non_ascii_payloads() {
        var dir = TmpDir();
        try {
            // Multi-byte UTF-8 bodies: char-count under-counts bytes, so a char-based cap would
            // let the file grow past capBytes. With byte-based counting the file stays bounded.
            var spool = new HookSpool(dir, capBytes: 400);
            for (var i = 0; i < 30; i++)
                spool.Append(SidA, "session-end", $$"""{"i":{{i}},"t":"日本語テキスト😀"}""");

            var path  = Path.Combine(dir, $"{SidA}.jsonl");
            var bytes = new FileInfo(path).Length;
            await Assert.That(bytes).IsLessThanOrEqualTo(400L);
            // Eviction happened (FIFO): newest entry retained, oldest dropped.
            var ids = (await File.ReadAllLinesAsync(path))
                .Select(l => JsonNode.Parse(l)!["body"]!.GetValue<string>())
                .Select(b => JsonNode.Parse(b)!["i"]!.GetValue<int>())
                .ToList();
            await Assert.That(ids.Count).IsLessThan(30);
            await Assert.That(ids).Contains(29);
            await Assert.That(ids).DoesNotContain(0);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
