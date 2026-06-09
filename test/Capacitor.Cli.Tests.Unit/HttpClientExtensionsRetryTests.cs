using System.Net;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class HttpClientExtensionsRetryTests {
    [Test]
    public async Task SendWithRetry_converts_per_attempt_timeout_to_HttpRequestException_after_budget_exhausted() {
        // Simulates the original bug: a slow server holds the request open past the
        // per-attempt cap. The retry loop must surface the failure as
        // HttpRequestException so the import's `catch (HttpRequestException)` blocks
        // can degrade gracefully instead of crashing with TaskCanceledException.
        var attempts = 0;

        async Task<HttpResponseMessage> Send(CancellationToken token) {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable — per-attempt CTS fires first.
        }

        var ex = await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromMilliseconds(400),
                    perAttemptTimeout: TimeSpan.FromMilliseconds(50),
                    ct: CancellationToken.None
                )
            )
            .Throws<HttpRequestException>();

        await Assert.That(ex!.InnerException).IsTypeOf<TaskCanceledException>();
        await Assert.That(attempts).IsGreaterThan(1);
    }

    [Test]
    public async Task SendWithRetry_retries_after_first_attempt_times_out_then_succeeds() {
        var attempts = 0;

        async Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            if (attempt == 1) {
                // First attempt: hang until the per-attempt CTS cancels it.
                await Task.Delay(Timeout.Infinite, token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send,
            totalTimeout: TimeSpan.FromSeconds(2),
            perAttemptTimeout: TimeSpan.FromMilliseconds(50),
            ct: CancellationToken.None
        );

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task SendWithRetry_propagates_caller_cancellation_without_converting_to_HttpRequestException() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        static Task<HttpResponseMessage> Send(CancellationToken token) {
            token.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromSeconds(1),
                    perAttemptTimeout: TimeSpan.FromSeconds(1),
                    ct: cts.Token
                )
            )
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task SendWithRetry_retries_transient_HttpRequestException_within_total_timeout() {
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            return attempt < 3
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connect refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send,
            totalTimeout: TimeSpan.FromSeconds(2),
            perAttemptTimeout: TimeSpan.FromSeconds(1),
            ct: CancellationToken.None
        );

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(3);
    }
}
