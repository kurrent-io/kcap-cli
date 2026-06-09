using System.Diagnostics;
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
                    totalTimeout: TimeSpan.FromMilliseconds(1_200),
                    perAttemptTimeout: TimeSpan.FromMilliseconds(150),
                    ct: CancellationToken.None
                )
            )
            .Throws<HttpRequestException>();

        await Assert.That(ex!.InnerException).IsTypeOf<TaskCanceledException>();
        await Assert.That(attempts).IsGreaterThan(1);
    }

    [Test]
    public async Task SendWithRetry_enforces_total_timeout_even_when_per_attempt_is_larger() {
        // Regression for Qodo finding: with total < per-attempt, a hung request
        // must not block past totalTimeout. The implementation caps each attempt
        // at min(perAttemptTimeout, remainingBudget) instead of always using the
        // full per-attempt cap.
        async Task<HttpResponseMessage> Send(CancellationToken token) {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        }

        var sw = Stopwatch.StartNew();

        await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromMilliseconds(300),
                    perAttemptTimeout: TimeSpan.FromSeconds(30),
                    ct: CancellationToken.None
                )
            )
            .Throws<HttpRequestException>();

        sw.Stop();
        // Generous upper bound: a strict enforcement should be ~300ms; even
        // under heavy CI load it should be well under 2s.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(2_000);
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
            totalTimeout: TimeSpan.FromSeconds(5),
            perAttemptTimeout: TimeSpan.FromMilliseconds(150),
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
            totalTimeout: TimeSpan.FromSeconds(5),
            perAttemptTimeout: TimeSpan.FromSeconds(1),
            ct: CancellationToken.None
        );

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(3);
    }
}
