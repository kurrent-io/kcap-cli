using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests;

public class PullRequestClientTests {
    [Test]
    public async Task Literal_contract_bundle_has_the_same_pinned_digest_as_the_server() {
        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(PullRequestContract.FixtureSha256);
    }

    [Test]
    public async Task Literal_responses_are_validated_through_each_typed_read_route() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        var subject = fixture.RootElement.GetProperty("subject").Deserialize(PullRequestJsonContext.Default.PullRequestSubjectDto)!;
        foreach (var example in fixture.RootElement.GetProperty("responses").EnumerateArray()) {
            using var handler = new Handler { Body = example.GetProperty("body").GetRawText() };
            using var http = new HttpClient(handler);
            using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
            var kind = example.GetProperty("route").GetString() switch {
                "links" => (await client.ListAsync("session", default)).Kind,
                "overview" => (await client.OverviewAsync("session", subject, default)).Kind,
                "checks" => (await client.PageAsync<PullRequestCheckDto>("session", subject, "checks", null, null, null, default)).Kind,
                "reviewers" => (await client.PageAsync<PullRequestReviewerDto>("session", subject, "reviewers", null, null, null, default)).Kind,
                "reviews" => (await client.PageAsync<PullRequestReviewDto>("session", subject, "reviews", null, null, null, default)).Kind,
                "threads" => (await client.PageAsync<PullRequestThreadDto>("session", subject, "threads", null, null, null, default)).Kind,
                "thread_comments" => (await client.PageAsync<PullRequestCommentDto>("session", subject, "thread_comments", null, null, "PRRT_thread", default)).Kind,
                _ => (await client.PageAsync<PullRequestCommentDto>("session", subject, "conversation", null, null, null, default)).Kind
            };
            await Assert.That(kind.ToString()).IsEqualTo(example.GetProperty("expected").GetString());
            await Assert.That(handler.Paths.Skip(1).All(path => path.Contains("version=1", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Discovery_requires_a_well_formed_shared_version_before_any_new_route() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        foreach (var example in fixture.RootElement.GetProperty("discovery").EnumerateArray()) {
            using var handler = new Handler { Discovery = example.GetProperty("body").GetRawText() };
            using var http = new HttpClient(handler);
            using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
            var discovery = await client.DiscoverAsync(false, default);
            await Assert.That(discovery.Kind.ToString()).IsEqualTo(example.GetProperty("kind").GetString());
            if (discovery.Kind != PullRequestCapabilityKind.Supported) {
                await client.ListAsync("session", default);
                await Assert.That(handler.Paths.Count).IsEqualTo(1);
            }
        }
    }

    [Test]
    public async Task Network_time_cannot_extend_a_response_access_lease() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        var subject = fixture.RootElement.GetProperty("subject").Deserialize(PullRequestJsonContext.Default.PullRequestSubjectDto)!;
        var overview = fixture.RootElement.GetProperty("responses")[1].GetProperty("body").GetRawText();
        var clock = new FakeTimeProvider();
        using var handler = new Handler { Body = overview, BeforeBody = () => clock.Advance(TimeSpan.FromSeconds(16)) };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", clock);
        var read = await client.OverviewAsync("session", subject, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.RemainingSeconds(clock)).IsEqualTo(4);
        await Assert.That(read.CanReveal(clock)).IsFalse();
    }

    [Test]
    public async Task Discovery_failures_back_off_without_turning_into_legacy_support() {
        var clock = new FakeTimeProvider();
        using var handler = new Handler { Discovery = "not-json" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", clock);
        await client.DiscoverAsync(false, default);
        await client.DiscoverAsync(true, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await client.DiscoverAsync(false, default);
        clock.Advance(TimeSpan.FromSeconds(59));
        await client.DiscoverAsync(true, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(2);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.DiscoverAsync(false, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_non_JSON_gateway_failure_is_transient_and_an_off_origin_response_is_invalid() {
        using var handler = new Handler { Status = HttpStatusCode.ServiceUnavailable, Body = "gateway unavailable" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        var outage = await client.ListAsync("session", default);
        await Assert.That(outage.AccessFailure).IsEqualTo("transient");
        handler.Redirect = true;
        var redirected = await client.ListAsync("session", default);
        await Assert.That(redirected.Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    public async Task Legacy_links_require_an_independently_admitted_session_summary() {
        using var handler = new Handler {
            Discovery = """{"provider":"workos"}""",
            Body = """{"session_id":"session","pull_requests":[{"repo_hash":"hash","owner":"Example","repo_name":"Repo","number":7,"url":"https://github.com/Example/Repo/pull/7"}]}"""
        };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        var read = await client.LegacyLinksAsync("session", default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.Data!.Items[0].Owner).IsEqualTo("example");
        await Assert.That(read.AccessValidForSeconds).IsEqualTo(0);
        await Assert.That(handler.Paths.Last()).IsEqualTo("/api/sessions/session/summary");
        handler.Status = HttpStatusCode.Forbidden;
        var denied = await client.LegacyLinksAsync("session", default);
        await Assert.That(denied.Data).IsNull();
        await Assert.That(denied.AccessFailure).IsEqualTo("invalid");
    }

    [Test]
    [Arguments("null")]
    [Arguments("[null]")]
    public async Task Malformed_legacy_rows_are_rejected(string rows) {
        using var handler = new Handler { Discovery = """{"provider":"workos"}""", Body = "{\"session_id\":\"session\",\"pull_requests\":" + rows + "}" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        await Assert.That((await client.LegacyLinksAsync("session", default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("true")]
    [Arguments("42")]
    public async Task Non_object_legacy_summaries_are_rejected_without_throwing(string body) {
        using var handler = new Handler { Discovery = """{"provider":"workos"}""", Body = body };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        await Assert.That((await client.LegacyLinksAsync("session", default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    public async Task A_small_response_is_read_with_a_buffer_sized_to_the_body() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        var subject = fixture.RootElement.GetProperty("subject").Deserialize(PullRequestJsonContext.Default.PullRequestSubjectDto)!;
        var overview = Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("responses")[1].GetProperty("body").GetRawText());
        var body = new RecordingStream(overview);
        using var handler = new Handler { Content = () => new StreamContent(body) };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        var read = await client.OverviewAsync("session", subject, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(body.LargestRead).IsLessThanOrEqualTo(64 * 1024);
    }

    /// A body streamed without a Content-Length is held to the same 4 MiB cap as a declared one:
    /// the last byte under the cap still parses, one byte over is refused as a protocol error.
    [Test]
    [Arguments(0, PullRequestReadKind.Ready)]
    [Arguments(1, PullRequestReadKind.InvalidProtocol)]
    public async Task The_response_cap_applies_to_a_body_streamed_without_a_content_length(int pastCap, PullRequestReadKind expected) {
        const int cap = 4 * 1024 * 1024;
        const string head = "{\"session_id\":\"session\",\"pull_requests\":[],\"pad\":\"";
        const string tail = "\"}";
        var padded = head + new string('x', cap + pastCap - head.Length - tail.Length) + tail;
        var bytes = Encoding.UTF8.GetBytes(padded);
        await Assert.That(bytes.Length).IsEqualTo(cap + pastCap);
        using var handler = new Handler { Discovery = """{"provider":"workos"}""", Content = () => new StreamContent(new RecordingStream(bytes)) };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", TimeProvider.System);
        await Assert.That((await client.LegacyLinksAsync("session", default)).Kind).IsEqualTo(expected);
    }

    /// The server refuses a read burst per user with a 429; the read carries when it may go again.
    [Test]
    public async Task A_refused_read_carries_the_servers_retry_time() {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        using var handler = new Handler { Status = (HttpStatusCode)429, RetryAfter = TimeSpan.FromSeconds(7),
            Body = """{"error":"read_rate_limited","retry_after_seconds":7}""" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", clock);

        var refused = await client.ListAsync("session", default);
        await Assert.That(refused.Kind).IsEqualTo(PullRequestReadKind.Unavailable);
        await Assert.That(refused.AccessFailure).IsEqualTo("transient");
        await Assert.That(refused.RetryAt).IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 7, DateTimeKind.Utc));

        handler.RetryAfter = null;
        await Assert.That((await client.ListAsync("session", default)).RetryAt).IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 1, DateTimeKind.Utc));

        handler.RetryAfter = TimeSpan.Zero;
        await Assert.That((await client.ListAsync("session", default)).RetryAt).IsEqualTo(new DateTime(2026, 10, 27, 12, 0, 1, DateTimeKind.Utc));

        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.That((await client.ListAsync("session", default)).RetryAt).IsNull();
    }

    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "pull-request-reads-v1.json");
    sealed class Handler : HttpMessageHandler {
        internal string Discovery = """{"provider":"workos","pull_request_reads_versions":[1]}""";
        internal string Body = "{}";
        internal Func<HttpContent>? Content;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal TimeSpan? RetryAfter;
        internal Action? BeforeBody;
        internal bool Redirect;
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var discovery = request.RequestUri.AbsolutePath == "/auth/config";
            if (!discovery) BeforeBody?.Invoke();
            var response = new HttpResponseMessage(discovery ? HttpStatusCode.OK : Status) {
                RequestMessage = Redirect ? new HttpRequestMessage(HttpMethod.Get, "https://foreign.test/result") : request,
                Content = !discovery && Content is { } content ? content() : new StringContent(discovery ? Discovery : Body, Encoding.UTF8, "application/json")
            };
            if (!discovery && RetryAfter is { } wait) response.Headers.RetryAfter = new(wait);
            return Task.FromResult(response);
        }
    }

    /// Non-seekable, so StreamContent declares no length and the client has to read it blind.
    sealed class RecordingStream(byte[] bytes) : Stream {
        readonly MemoryStream _inner = new(bytes, writable: false);
        internal int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return _inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
