using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1357 task 8: at shutdown (idle-timeout / parent-exit) with the hub down, the final drain's
/// undelivered transcript tail (state.LinesProcessed → EOF) must not be silently dropped — it is
/// spooled into the dedicated <see cref="TranscriptSpool"/> so the global drain (task 3) replays it
/// after recovery, without a manual `kcap import`.
/// </summary>
public class ShutdownTranscriptSpoolTests {
    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    static string TmpDir(string prefix) => Path.Combine(Path.GetTempPath(), $"kcap-{prefix}-{Guid.NewGuid():N}");

    [Test]
    public async Task build_batch_serializes_lines_and_vendor() {
        var json = WatchCommand.BuildTranscriptSpoolBatch(Sid, null, "kiro", ["{\"k\":1}"], [7]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(node["vendor"]!.GetValue<string>()).IsEqualTo("kiro");
        await Assert.That(node["line_numbers"]![0]!.GetValue<int>()).IsEqualTo(7);
        await Assert.That(node["lines"]![0]!.GetValue<string>()).IsEqualTo("{\"k\":1}");
    }

    [Test]
    public async Task build_batch_includes_agent_id_when_present() {
        var json = WatchCommand.BuildTranscriptSpoolBatch(Sid, "agent-1", "codex", ["{\"k\":1}"], [0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        await Assert.That(node["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
    }

    [Test]
    public async Task tail_spooled_when_hub_down_at_shutdown() {
        var dir = TmpDir("shut");
        try {
            var tx    = new TranscriptSpool(dir);
            var batch = WatchCommand.BuildTranscriptSpoolBatch(Sid, null, "kiro", ["{\"k\":1}"], [0]);
            var r     = tx.Append(Sid, batch);
            await Assert.That(r).IsEqualTo(TranscriptSpool.AppendResult.Appended);
            await Assert.That(tx.HasBacklog(Sid)).IsTrue();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Full shutdown-during-outage path: a transcript file has lines beyond
    /// <c>state.LinesProcessed</c> (never confirmed sent because the hub was down), and
    /// <see cref="WatchCommand.SpoolUndeliveredTranscriptTailAsync"/> reads exactly that tail and
    /// spools it — never touching lines already confirmed delivered.
    /// </summary>
    [Test]
    public async Task shutdown_spools_only_the_undelivered_tail() {
        var dir          = TmpDir("shut-tail");
        var spoolDir     = TmpDir("shut-tail-spool");
        var transcriptPath = Path.Combine(dir, "transcript.jsonl");

        try {
            Directory.CreateDirectory(dir);
            // Lines 0-1 already confirmed sent (LinesProcessed = 2); lines 2-3 are the undelivered
            // tail that the outage left behind at shutdown.
            await File.WriteAllTextAsync(transcriptPath,
                "{\"line\":0}\n{\"line\":1}\n{\"line\":2}\n{\"line\":3}\n");

            var spool  = new TranscriptSpool(spoolDir);
            var result = await WatchCommand.SpoolUndeliveredTranscriptTailAsync(
                spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 2, CancellationToken.None);

            await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);
            await Assert.That(spool.HasBacklog(Sid)).IsTrue();

            // Replay via the same DrainAsync path the global drain (task 3) uses — only the
            // undelivered tail (lines 2 and 3) is replayed, in order, nothing from before LinesProcessed.
            var replayed = new List<string>();
            await spool.DrainAsync(Sid, body => {
                replayed.Add(body);
                return Task.FromResult(DrainOutcome.Delivered);
            }, () => false, CancellationToken.None);

            await Assert.That(replayed).HasCount().EqualTo(1);
            var node  = System.Text.Json.Nodes.JsonNode.Parse(replayed[0])!;
            var lines = node["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
            await Assert.That(lines).IsEquivalentTo(["{\"line\":2}", "{\"line\":3}"]);
            await Assert.That(spool.HasBacklog(Sid)).IsFalse();
        } finally {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(spoolDir, true); } catch { }
        }
    }

    [Test]
    public async Task shutdown_with_nothing_undelivered_is_a_noop() {
        var dir             = TmpDir("shut-empty");
        var spoolDir        = TmpDir("shut-empty-spool");
        var transcriptPath  = Path.Combine(dir, "transcript.jsonl");

        try {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(transcriptPath, "{\"line\":0}\n{\"line\":1}\n");

            var spool  = new TranscriptSpool(spoolDir);
            // LinesProcessed already at EOF — the final drain sent everything before the hub went down.
            var result = await WatchCommand.SpoolUndeliveredTranscriptTailAsync(
                spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 2, CancellationToken.None);

            await Assert.That(result).IsNull();
            await Assert.That(spool.HasBacklog(Sid)).IsFalse();
        } finally {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(spoolDir, true); } catch { }
        }
    }

    /// <summary>Cap exhaustion at shutdown must never silently drop the tail — it marks needs-import
    /// (surfaced by the global drain as a `kcap import` recovery hint) instead.</summary>
    [Test]
    public async Task shutdown_tail_exceeding_cap_marks_needs_import() {
        var dir            = TmpDir("shut-cap");
        var spoolDir       = TmpDir("shut-cap-spool");
        var transcriptPath = Path.Combine(dir, "transcript.jsonl");

        try {
            Directory.CreateDirectory(dir);
            var bigLine = "{\"line\":0,\"pad\":\"" + new string('x', 200) + "\"}";
            await File.WriteAllTextAsync(transcriptPath, bigLine + "\n");

            var spool  = new TranscriptSpool(spoolDir, capBytes: 64); // tiny cap — the batch can't fit
            var result = await WatchCommand.SpoolUndeliveredTranscriptTailAsync(
                spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 0, CancellationToken.None);

            await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
            await Assert.That(spool.NeedsImport(Sid)).IsTrue();
        } finally {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(spoolDir, true); } catch { }
        }
    }

    [Test]
    public async Task shutdown_missing_transcript_file_is_a_noop() {
        var spoolDir = TmpDir("shut-missing-spool");
        try {
            var spool  = new TranscriptSpool(spoolDir);
            var result = await WatchCommand.SpoolUndeliveredTranscriptTailAsync(
                spool, "/tmp/kcap-nonexistent-" + Guid.NewGuid(), Sid, agentId: null, vendor: "kiro",
                linesProcessed: 0, CancellationToken.None);

            await Assert.That(result).IsNull();
        } finally { try { Directory.Delete(spoolDir, true); } catch { } }
    }
}
