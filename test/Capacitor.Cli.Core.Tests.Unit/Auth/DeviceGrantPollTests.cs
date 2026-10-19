using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class DeviceGrantPollTests {
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // interval 0 so the loop does not really wait; the clock only has to move the deadline.
    static HttpResponseMessage DeviceCode(int expiresIn) =>
        Json($$"""{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0,"expires_in":{{expiresIn}}}""");

    /// <summary>
    /// The loop was <c>while (true)</c>: a code the server had already discarded was polled forever,
    /// so a user who abandoned the browser left the CLI running until they noticed.
    /// </summary>
    [Test]
    public async Task Stops_polling_once_the_device_code_has_expired() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 2);

            polls++;

            return Json("""{"error":"authorization_pending"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));

        // Every clock read moves a second, so the 2-second deadline is reached within a few polls.
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(), progress: new RecordingAuthProgress(), time: time);

        await Assert.That(token).IsNull();
        await Assert.That(polls).IsLessThan(5);   // bounded, not forever
    }

    /// <summary>
    /// The response was deserialized without checking the status and force-unwrapped, so a 429 or a
    /// 5xx HTML error page was a NullReferenceException mid-sign-in rather than a backoff.
    /// </summary>
    [Test]
    public async Task Backs_off_and_keeps_polling_when_the_token_endpoint_returns_an_unreadable_body() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 900);

            polls++;

            // A gateway's HTML, not JSON — exactly what used to NRE.
            return polls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                      Content = new StringContent("<html><body>Too Many Requests</body></html>", Encoding.UTF8, "text/html")
                  }
                : Json("""{"access_token":"tok"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(), progress: new RecordingAuthProgress(),
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(polls).IsEqualTo(2);
    }

    sealed class HangsOnceHandler : HttpMessageHandler {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (++Calls == 1) {
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Json("""{"access_token":"acc"}""");
        }
    }

    sealed class HangingBodyContent : HttpContent {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);

            throw new InvalidOperationException("The body must wait for cancellation.");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length) {
            length = 0;

            return false;
        }
    }

    static Task<string?> Poll(
            Func<Dictionary<string, string>, CancellationToken, Task<HttpResponseMessage>> post,
            RecordingAuthProgress progress, TimeProvider time, CancellationToken ct = default) =>
        OAuthLoginFlow.PollDeviceGrantAsync(
            post, new() { ["client_id"] = "c" },
            CapacitorJsonContext.Default.GitHubTokenResponse,
            r => (r.AccessToken, r.Error),
            new DeviceCodeResponse { DeviceCode = "dc", ExpiresIn = 900, Interval = 0 },
            interval: 0, ct, progress, time);

    [Test]
    public async Task A_hung_poll_is_retried_rather_than_ending_the_sign_in() {
        using var handler = new HangsOnceHandler();
        var github        = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var progress      = new RecordingAuthProgress();
        var time          = new FakeTimeProvider();

        var polling = Poll(github.PollForTokenAsync, progress, time);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(20));
        var token = await polling.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(token).IsEqualTo("acc");
        await Assert.That(handler.Calls).IsEqualTo(2);
        await Assert.That(progress.PollTicks).IsEqualTo(1);
        await Assert.That(progress.Errors).IsEmpty();
    }

    [Test]
    public async Task A_timed_out_success_response_body_is_retried() {
        using var body = new HangingBodyContent();
        var time       = new FakeTimeProvider();
        var progress   = new RecordingAuthProgress();
        var calls      = 0;

        var polling = Poll((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = body }
            : Json("""{"access_token":"acc"}""")), progress, time);

        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(20));
        var token = await polling.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(token).IsEqualTo("acc");
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(progress.PollTicks).IsEqualTo(1);
        await Assert.That(progress.Errors).IsEmpty();
    }

    [Test]
    public async Task Cancelling_while_reading_a_response_stops_sign_in() {
        using var body = new HangingBodyContent();
        using var ct   = new CancellationTokenSource();
        var progress   = new RecordingAuthProgress();
        var calls      = 0;

        var polling = Poll((_, _) => {
            calls++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }, progress, new FakeTimeProvider(), ct.Token);

        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await ct.CancelAsync();

        await Assert.That(async () => await polling.WaitAsync(TimeSpan.FromSeconds(30)))
            .Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(progress.PollTicks).IsEqualTo(0);
        await Assert.That(progress.Errors).IsEmpty();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("null")]
    public async Task An_unreadable_success_response_ends_sign_in(string body) {
        var progress = new RecordingAuthProgress();
        var calls    = 0;

        var token = await Poll((_, _) => {
            calls++;

            return Task.FromResult(Json(body));
        }, progress, new FakeTimeProvider()).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(token).IsNull();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(progress.PollTicks).IsEqualTo(0);
        await Assert.That(progress.Errors.Count).IsEqualTo(1);
        await Assert.That(progress.Errors[0]).Contains("unreadable response");
    }

    /// <summary>
    /// A parsed body is not a verdict. A 5xx that happens to be JSON deserializes into a response with
    /// every field defaulted, so it reads as neither a token nor a known RFC 8628 error - and only the
    /// status separates "the service is having a moment" from "the service answered you".
    /// </summary>
    [Test]
    public async Task Keeps_polling_when_a_readable_5xx_carries_no_device_error() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 900);

            polls++;

            return polls == 1
                ? Json("""{"message":"temporarily unavailable"}""", HttpStatusCode.InternalServerError)
                : Json("""{"access_token":"tok"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var       progress = new RecordingAuthProgress();

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(opens: false), progress: progress,
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(polls).IsEqualTo(2);
        await Assert.That(progress.Errors).IsEmpty();
    }

    /// <summary>The other half of the same rule: on a 2xx an unrecognised code IS the answer.</summary>
    [Test]
    public async Task An_unrecognised_error_on_a_success_status_still_ends_the_sign_in() {
        using var handler = new Handler(request =>
            request.RequestUri!.AbsolutePath.Contains("device/code")
                ? DeviceCode(expiresIn: 900)
                : Json("""{"error":"device_flow_disabled"}"""));
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var       progress = new RecordingAuthProgress();

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(opens: false), progress: progress,
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsNull();
        await Assert.That(string.Join("\n", progress.Errors)).Contains("device_flow_disabled");
    }
}
