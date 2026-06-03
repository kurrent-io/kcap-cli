using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Cursor;

public class CursorTranscriptBackfillTests {
    [Test]
    public async Task RunAsync_returns_zero_when_transcript_path_null() {
        using var tmp = new TempDir();
        using var handler = new RecordingHandler();
        using var client  = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: null, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(handler.Sent).IsEmpty();
    }

    [Test]
    public async Task RunAsync_resumes_from_last_line_number_plus_one_and_posts_single_batch() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["line0", "line1", "line2", "line3"]);

        string? postedBody = null;
        string? postedPath = null;
        using var handler = new RecordingHandler(
            getResponse: req => req.RequestUri!.AbsolutePath.EndsWith("/last-line")
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"last_line_number":1}""")
                }
                : null,
            postCapture: (req, body) => {
                postedPath = req.RequestUri!.AbsolutePath;
                postedBody = body;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(postedPath).IsEqualTo("/hooks/transcript");

        var node = JsonNode.Parse(postedBody!)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(node["vendor"]!.GetValue<string>()).IsEqualTo("cursor");
        var lines = node["lines"]!.AsArray();
        var lineNumbers = node["line_numbers"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]!.GetValue<string>()).IsEqualTo("line2");
        await Assert.That(lines[1]!.GetValue<string>()).IsEqualTo("line3");
        await Assert.That(lineNumbers[0]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(lineNumbers[1]!.GetValue<int>()).IsEqualTo(3);
    }

    [Test]
    public async Task RunAsync_treats_204_NoContent_watermark_as_resume_from_zero() {
        // /api/sessions/{sid}/last-line returns 204 when the session stream
        // exists but no lines have been accepted yet. Treat as resumeFrom=0,
        // not as failure.
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["line0", "line1"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NoContent),
            postCapture: (_, _) => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(stats.Failed).IsFalse();
    }

    [Test]
    public async Task RunAsync_does_not_post_when_budget_already_expired() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b", "c"]);

        var postCount = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NotFound),
            postCapture: (_, _) => {
                postCount++;
                return new(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript,
            budget: () => true,
            CancellationToken.None);

        // Budget already burnt before the batch POST — nothing posted, no failure.
        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsFalse();
        await Assert.That(postCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_returns_failed_on_POST_non_2xx() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b", "c"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NotFound),
            postCapture: (_, _) => new(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsTrue();
    }

    [Test]
    public async Task RunAsync_fails_open_on_watermark_GET_5xx() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.InternalServerError),
            postCapture: (_, _) => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsTrue();
    }

    sealed class RecordingHandler : HttpMessageHandler {
        readonly Func<HttpRequestMessage, HttpResponseMessage?>? _get;
        readonly Func<HttpRequestMessage, string, HttpResponseMessage>? _post;
        public List<string> Sent { get; } = new();

        public RecordingHandler() { }
        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage?>? getResponse,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture) {
            _get  = getResponse;
            _post = postCapture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get) {
                return _get?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Sent.Add(body);
            return _post?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-backfill-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
