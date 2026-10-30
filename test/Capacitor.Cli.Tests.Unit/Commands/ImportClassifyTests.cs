using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportClassifyTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public ImportClassifyTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    static async Task<string> WriteTranscript(string dir, string sessionId, int lines) {
        var path = Path.Combine(dir, $"{sessionId}.jsonl");

        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(0, lines)
                .Select(i =>
                    $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
                )
        );

        return path;
    }

    [Test]
    public async Task ClassifyAsync_maps_404_to_New() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "sessionNew", lines: 50);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionNew", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(result[0].SessionId).IsEqualTo("sessionNew");
        // TotalLines is only populated for TooShort; other statuses early-exit before counting.
        await Assert.That(result[0].TotalLines).IsEqualTo(0);
    }

    [Test]
    public async Task ClassifyAsync_maps_204_to_AlreadyLoaded() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(204));

        var path = await WriteTranscript(_tempDir, "sessionDone", lines: 50);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionDone", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task ClassifyAsync_maps_200_with_last_line_to_Partial() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 42}""")
            );

        var path = await WriteTranscript(_tempDir, "sessionPartial", lines: 100);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionPartial", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(43);
    }

    [Test]
    public async Task ClassifyAsync_maps_short_transcript_to_TooShort() {
        // TooShort is decided after the probe, only for sessions that would otherwise be
        // New or Partial, so AlreadyLoaded re-runs don't pay for scanning huge transcripts.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var path = await WriteTranscript(_tempDir, "tiny", lines: 5);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("tiny", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
        await Assert.That(result[0].TotalLines).IsEqualTo(5);
    }

    [Test]
    public async Task ClassifyAsync_maps_server_error_to_ProbeError() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var path = await WriteTranscript(_tempDir, "sessionErr", lines: 50);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("sessionErr", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task ClassifyAsync_identifies_kcap_subsession() {
        // Nested under _tempDir so Dispose cleans it up.
        var subagentDir = Path.Combine(_tempDir, "kcap-sub");
        Directory.CreateDirectory(subagentDir);
        var path = Path.Combine(subagentDir, "agent-title-abc123.jsonl");
        // \n must be JSON-escaped so the parser sees a literal newline in the prompt content.
        var queueOpLine = """{"type":"queue-operation","operation":"enqueue","content":"<role>\nYou label coding-session transcripts. You are NOT the assistant being addressed"}""";
        await File.WriteAllLinesAsync(path, [queueOpLine]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("title-abc123", path, "-tmp-sub")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            transcripts,
            15,
            CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.InternalSubSession);
    }

    [Test]
    public async Task ClassifyAsync_invokes_onProbed_callback_once_per_transcript() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var paths = new List<(string SessionId, string FilePath, string EncodedCwd)>();
        for (var i = 0; i < 5; i++) {
            var path = await WriteTranscript(_tempDir, $"cb-{i}", lines: 50);
            paths.Add(($"cb-{i}", path, "-tmp-proj"));
        }

        var probedCount = 0;
        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client,
            TimeProvider.System,
            _server.Url!,
            paths,
            15,
            CancellationToken.None,
            onProbed: () => Interlocked.Increment(ref probedCount)
        );

        await Assert.That(result.Count).IsEqualTo(5);
        await Assert.That(probedCount).IsEqualTo(5);
    }

    [Test]
    public async Task ClassifyAsync_reclassifies_Partial_to_AlreadyLoaded_when_no_new_lines() {
        // last_line_number=49 covers all 50 local lines (indices 0..49), so despite
        // the 200 response this is a false Partial and must reclassify to AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        var path = await WriteTranscript(_tempDir, "noNewLines", lines: 50);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("noNewLines", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client, TimeProvider.System, _server.Url!, transcripts,
            minLines: 15, ct: CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(0);
    }

    [Test]
    public async Task ClassifyAsync_keeps_Partial_when_local_transcript_has_new_lines() {
        // Server says last_line_number = 49. Local transcript is 60 lines —
        // there are 10 new lines past index 49, so Partial is correct.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": 49}""")
            );

        var path = await WriteTranscript(_tempDir, "hasNewLines", lines: 60);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("hasNewLines", path, "-tmp-proj")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client, TimeProvider.System, _server.Url!, transcripts,
            minLines: 15, ct: CancellationToken.None
        );

        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result[0].ResumeFromLine).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_maps_410_to_Deleted_not_a_probe_error() {
        StubLastLine(410, """{"error":"session_deleted"}""");

        var path = await WriteTranscript(_tempDir, "sessionDeleted", lines: 50);

        var result = await Classify(("sessionDeleted", path));

        await Assert.That(result.Status).IsEqualTo(ImportCommand.ClassificationStatus.Deleted);
        await Assert.That(result.ProbeErrorReason).IsNull();
    }

    const string ClaudeTurnEnd =
        """{"type":"system","subtype":"stop_hook_summary","timestamp":"2026-03-15T10:01:00Z"}""" + "\n"
      + """{"type":"system","subtype":"turn_duration","durationMs":1200,"timestamp":"2026-03-15T10:01:01Z"}""" + "\n"
      + """{"type":"last-prompt","lastPrompt":"line-49","sessionId":"s"}""" + "\n"
      + """{"type":"ai-title","aiTitle":"Retry the import","sessionId":"s"}""" + "\n";

    /// <summary>The server's watermark stops at the last line that stored an event; the turn-end records
    /// after it never move it, so they must not make a complete session resumable.</summary>
    [Test]
    public async Task ClassifyAsync_reads_a_Claude_transcript_ending_in_turn_end_records_as_AlreadyLoaded() {
        StubLastLine(200, """{"last_line_number": 49}""");

        var path = await WriteTranscript(_tempDir, "claudeComplete", lines: 50);
        await File.AppendAllTextAsync(path, ClaudeTurnEnd);

        var result = await Classify(("claudeComplete", path), ClaudeEventLines.CanAddEvents);

        await Assert.That(result.Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result.ResumeFromLine).IsEqualTo(0);
    }

    [Test]
    public async Task ClassifyAsync_resumes_a_Claude_transcript_that_grew_from_the_line_after_the_watermark() {
        StubLastLine(200, """{"last_line_number": 49}""");

        var path = await WriteTranscript(_tempDir, "claudeGrew", lines: 50);
        await File.AppendAllTextAsync(path, ClaudeTurnEnd
          + """{"type":"user","timestamp":"2026-03-15T11:00:00Z","cwd":"/tmp/proj","message":{"content":"one more thing"}}""" + "\n");

        var result = await Classify(("claudeGrew", path), ClaudeEventLines.CanAddEvents);

        await Assert.That(result.Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result.ResumeFromLine).IsEqualTo(50);
    }

    [Test]
    public async Task ClassifyAsync_reads_trailing_blank_lines_as_nothing_to_resume() {
        StubLastLine(200, """{"last_line_number": 49}""");

        var path = await WriteTranscript(_tempDir, "blankTail", lines: 50);
        await File.AppendAllTextAsync(path, "\n   \n");

        var result = await Classify(("blankTail", path));

        await Assert.That(result.Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task ClassifyAsync_reads_a_Codex_rollout_ending_in_event_msg_records_as_AlreadyLoaded() {
        StubLastLine(200, """{"last_line_number": 1}""");

        var id   = "019a0000-0000-7000-8000-000000000001";
        var path = Path.Combine(_tempDir, $"rollout-2026-03-15T10-00-00-{id}.jsonl");
        await File.WriteAllLinesAsync(path, [
            $$$"""{"timestamp":"2026-03-15T10:00:00Z","type":"session_meta","payload":{"id":"{{{id}}}","cwd":"/tmp/proj"}}""",
            """{"timestamp":"2026-03-15T10:00:01Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}""",
            """{"timestamp":"2026-03-15T10:00:02Z","type":"event_msg","payload":{"type":"token_count","info":null}}""",
            """{"timestamp":"2026-03-15T10:00:03Z","type":"event_msg","payload":{"type":"task_complete"}}""",
        ]);

        var result = await Classify((id.Replace("-", ""), path), CodexEventLines.CanAddEvents, HarnessId.Codex, minLines: 1);

        await Assert.That(result.Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    void StubLastLine(int status, string body) =>
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    async Task<ImportCommand.SessionClassification> Classify(
            (string SessionId, string FilePath) transcript,
            Func<string, bool>?                 canAddEvents = null,
            HarnessId                           vendor       = HarnessId.Claude,
            int                                 minLines     = 15
        ) {
        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(),
            Config.Root,
            Home,
            client, TimeProvider.System, _server.Url!,
            [(transcript.SessionId, transcript.FilePath, "-tmp-proj")],
            minLines, CancellationToken.None,
            vendor: vendor,
            canAddEvents: canAddEvents
        );

        return result.Single();
    }
}
