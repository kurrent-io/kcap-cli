using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class LifecycleSpoolDrainTests {
    static string TmpDir() => Path.Combine(Path.GetTempPath(), $"kcap-drain-{Guid.NewGuid():N}");
    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task drains_start_then_transcript_then_end_for_a_session_with_no_further_hook() {
        var dir = TmpDir();
        try {
            var life = new HookSpool(Path.Combine(dir, "spool"));
            var tx   = new TranscriptSpool(Path.Combine(dir, "tx"));
            life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
            tx.Append(Sid, """{"phase":"tail"}""");
            life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

            var order = new List<string>();
            // currentSessionId=null → a DIFFERENT vendor's kcap invocation replaying a prior session.
            await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
                lifecyclePoster: (route, body) => { order.Add($"L:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
                transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
                budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

            await Assert.That(order).IsEquivalentTo([
                """L:{"phase":"start"}""",
                """T:{"phase":"tail"}""",
                """L:{"phase":"end"}""",
            ]);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task holds_session_end_when_transcript_backlog_remains() {
        var dir = TmpDir();
        try {
            var life = new HookSpool(Path.Combine(dir, "spool"));
            var tx   = new TranscriptSpool(Path.Combine(dir, "tx"));
            life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
            tx.Append(Sid, """{"phase":"tail"}""");
            life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

            var order = new List<string>();
            await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
                lifecyclePoster: (route, body) => { order.Add($"L:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
                // Transcript poster reports a transient failure — the tail never fully drains.
                transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.TransientStop); },
                budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

            // session-start delivered, transcript attempted, but session-end withheld because the
            // transcript still has backlog — cross-spool ordering must not let session-end race ahead.
            await Assert.That(order).IsEquivalentTo([
                """L:{"phase":"start"}""",
                """T:{"phase":"tail"}""",
            ]);
            await Assert.That(tx.HasBacklog(Sid)).IsTrue();
            await Assert.That(life.HasBacklog(Sid)).IsTrue(); // session-end still spooled
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task delivers_needs_import_marker_even_when_transcript_bytes_exceeded_cap() {
        var dir = TmpDir();
        try {
            var life = new HookSpool(Path.Combine(dir, "spool"));
            var tx   = new TranscriptSpool(Path.Combine(dir, "tx"), capBytes: 32); // tiny cap
            life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
            tx.Append(Sid, "{\"lines\":[\"" + new string('x', 100) + "\"]}"); // exceeds cap → needs-import marker
            life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

            await Assert.That(tx.NeedsImport(Sid)).IsTrue();

            var order = new List<string>();
            await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
                lifecyclePoster: (route, body) => { order.Add($"L:{route}:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
                transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
                budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

            await Assert.That(order).IsEquivalentTo([
                """L:session-start/kiro:{"phase":"start"}""",
                $"L:session-needs-import:{{\"session_id\":\"{Sid}\",\"needs_import\":true}}",
                """L:session-end/kiro:{"phase":"end"}""",
            ]);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task current_session_id_drains_first() {
        var dir = TmpDir();
        try {
            var life = new HookSpool(Path.Combine(dir, "spool"));
            var tx   = new TranscriptSpool(Path.Combine(dir, "tx"));
            const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            life.Append(other, "session-start/kiro", """{"who":"other"}""");
            life.Append(Sid, "session-start/kiro", """{"who":"current"}""");

            var order = new List<string>();
            await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: Sid,
                lifecyclePoster: (route, body) => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

            await Assert.That(order[0]).IsEqualTo("""{"who":"current"}""");
            await Assert.That(order).Contains("""{"who":"other"}""");
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
