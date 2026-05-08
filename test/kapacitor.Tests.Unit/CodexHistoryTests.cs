using System.Text.Json;
using kapacitor;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHistoryTests {
    [Test]
    public async Task ExtractCodexSessionMetadata_pulls_cwd_model_provider_and_first_timestamp() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"timestamp":"2026-05-07T15:51:46.684Z","type":"session_meta","payload":{"id":"019e0322-05fc-7570-be65-75719c3ea861","timestamp":"2026-05-07T15:50:21.989Z","cwd":"/Users/alexey/dev/temp/Kurrent.Capacitor","originator":"codex-tui","cli_version":"0.128.0","model_provider":"openai","git":{"commit_hash":"abc","branch":"main","repository_url":"https://github.com/owner/repo"}}}""",
                """{"timestamp":"2026-05-07T15:51:46.686Z","type":"event_msg","payload":{"type":"task_started"}}""",
            ]);

            var meta = HistoryCommand.ExtractCodexSessionMetadata(path);

            await Assert.That(meta.Cwd).IsEqualTo("/Users/alexey/dev/temp/Kurrent.Capacitor");
            await Assert.That(meta.Model).IsEqualTo("openai");
            await Assert.That(meta.SessionId).IsEqualTo("019e0322-05fc-7570-be65-75719c3ea861");
            await Assert.That(meta.FirstTimestamp).IsNotNull();
            await Assert.That(meta.FirstTimestamp!.Value).IsEqualTo(DateTimeOffset.Parse("2026-05-07T15:50:21.989Z"));
            await Assert.That(meta.Slug).IsNull();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractCodexSessionMetadata_returns_empty_when_first_line_is_not_session_meta() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"event_msg","payload":{"type":"task_started"}}""",
            ]);

            var meta = HistoryCommand.ExtractCodexSessionMetadata(path);

            await Assert.That(meta.Cwd).IsNull();
            await Assert.That(meta.Model).IsNull();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractCodexGitInfo_returns_git_block_when_present() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"session_meta","payload":{"id":"x","cwd":"/x","git":{"commit_hash":"deadbeef","branch":"main","repository_url":"https://github.com/owner/repo"}}}""",
            ]);

            var git = HistoryCommand.ExtractCodexGitInfo(path);

            await Assert.That(git).IsNotNull();
            await Assert.That(git!.RemoteUrl).IsEqualTo("https://github.com/owner/repo");
            await Assert.That(git.Branch).IsEqualTo("main");
            await Assert.That(git.CommitHash).IsEqualTo("deadbeef");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractCodexGitInfo_returns_null_when_no_git_block() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"session_meta","payload":{"id":"x","cwd":"/x"}}""",
            ]);

            var git = HistoryCommand.ExtractCodexGitInfo(path);

            await Assert.That(git).IsNull();
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractCodexTitleContext_skips_environment_context_and_returns_first_real_user_text() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"session_meta","payload":{"id":"x","cwd":"/x"}}""",
                """{"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"<permissions instructions>..."}]}}""",
                """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>foo</environment_context>"}]}}""",
                """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"review combined work in PR 573"}]}}""",
                """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"I will inspect both PRs as one change set."}]}}""",
            ]);

            var (userText, assistantText) = TitleGenerator.ExtractCodexTitleContext(path);

            await Assert.That(userText).IsEqualTo("review combined work in PR 573");
            await Assert.That(assistantText).IsEqualTo("I will inspect both PRs as one change set.");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractCodexTitleContext_returns_null_user_when_only_environment_context_present() {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllLinesAsync(path, [
                """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>only</environment_context>"}]}}""",
            ]);

            var (userText, assistantText) = TitleGenerator.ExtractCodexTitleContext(path);

            await Assert.That(userText).IsNull();
            await Assert.That(assistantText).IsNull();
        } finally {
            File.Delete(path);
        }
    }

    // Wire-shape: vendor field is omitted when null and serialized as "vendor":"codex" otherwise.
    // The server's INormalizerSelector keys off this exact field — see Kurrent.Capacitor#576.

    [Test]
    public async Task TranscriptBatch_omits_vendor_field_when_null() {
        var batch = new TranscriptBatch {
            SessionId = "abc",
            Lines     = ["{}"],
            Vendor    = null,
        };

        var json = JsonSerializer.Serialize(batch, KapacitorJsonContext.Default.TranscriptBatch);

        await Assert.That(json.Contains("\"vendor\"")).IsFalse();
    }

    [Test]
    public async Task ClassifyAsync_returns_ProbeError_when_codex_filename_uuid_disagrees_with_session_meta_id() {
        using var server = WireMockServer.Start();
        // Stub /last-line in case the short-circuit ever regresses — the test should
        // fail loudly rather than time out on a real network call.
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var dir = Directory.CreateTempSubdirectory("codex-id-mismatch").FullName;

        try {
            var path = Path.Combine(dir, "rollout.jsonl");
            // Filename-derived sessionId we'll pass in: 019e0322...c3ea861 (dashless).
            // Inner payload.id is a different UUID — the validator must catch this.
            await File.WriteAllLinesAsync(path, [
                """{"type":"session_meta","payload":{"id":"00000000-0000-0000-0000-000000000001","cwd":"/x"}}""",
            ]);

            var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
                ("019e032205fc7570be6575719c3ea861", path, "")
            };

            using var client = new HttpClient();

            var result = await HistoryCommand.ClassifyAsync(
                client,
                server.Url!,
                transcripts,
                minLines: 0,
                excludedRepos: null,
                CancellationToken.None,
                vendor: "codex"
            );

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.ProbeError);
            await Assert.That(result[0].ProbeErrorReason).IsNotNull();
            await Assert.That(result[0].ProbeErrorReason!).Contains("session id mismatch");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ClassifyAsync_accepts_codex_session_when_filename_and_session_meta_id_match() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var dir = Directory.CreateTempSubdirectory("codex-id-match").FullName;

        try {
            var path = Path.Combine(dir, "rollout.jsonl");
            // Filename uuid (dashless) and session_meta payload.id (dashed) refer to
            // the same GUID — validator must let this through to the server probe.
            await File.WriteAllLinesAsync(path, [
                """{"type":"session_meta","payload":{"id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/x"}}""",
            ]);

            var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
                ("019e032205fc7570be6575719c3ea861", path, "")
            };

            using var client = new HttpClient();

            var result = await HistoryCommand.ClassifyAsync(
                client,
                server.Url!,
                transcripts,
                minLines: 0,
                excludedRepos: null,
                CancellationToken.None,
                vendor: "codex"
            );

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].Status).IsEqualTo(HistoryCommand.ClassificationStatus.New);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task TranscriptBatch_serializes_vendor_when_set() {
        var batch = new TranscriptBatch {
            SessionId = "abc",
            Lines     = ["{}"],
            Vendor    = "codex",
        };

        var json = JsonSerializer.Serialize(batch, KapacitorJsonContext.Default.TranscriptBatch);

        await Assert.That(json.Contains("\"vendor\":\"codex\"")).IsTrue();
    }
}
