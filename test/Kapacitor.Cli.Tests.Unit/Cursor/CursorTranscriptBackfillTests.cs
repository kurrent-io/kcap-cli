using System.Net;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

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
    public async Task RunAsync_resumes_from_last_line_number_plus_one() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "line0", "line1", "line2", "line3" });

        var posted = new List<(int Index, string Line)>();
        using var handler = new RecordingHandler(
            getResponse: req => req.RequestUri!.AbsolutePath.EndsWith("/transcript-watermark")
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"last_line_number":1}""")
                }
                : null,
            postCapture: (req, body) => {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body)!;
                posted.Add(((int)node["line_index"]!.GetValue<int>(), node["line"]!.GetValue<string>()));
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(posted[0]).IsEqualTo((2, "line2"));
        await Assert.That(posted[1]).IsEqualTo((3, "line3"));
    }

    [Test]
    public async Task RunAsync_stops_when_budget_expires() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, Enumerable.Range(0, 50).Select(i => $"line{i}"));

        var posted = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                posted++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript,
            budget: () => posted >= 3,
            CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(3);
    }

    [Test]
    public async Task RunAsync_stops_on_first_POST_failure() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "a", "b", "c" });

        var posted = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                posted++;
                return new HttpResponseMessage(posted < 2 ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(1);
        await Assert.That(stats.Failed).IsTrue();
    }

    [Test]
    public async Task RunAsync_fails_open_on_watermark_GET_failure() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, new[] { "a", "b" });

        using var handler = new RecordingHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
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
