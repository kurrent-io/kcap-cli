using System.Net.Sockets;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap sessions</c> against a server that never answers: a failed listing exits nonzero, and its
/// one stderr line says why the connection failed and what fixes that, never a sign-in.
/// </summary>
[NotInParallel]
public class SessionsCommandUnreachableTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Server = "http://127.0.0.1:9";

    async Task<(int Exit, string Stderr, int Attempts)> RunAsync(Exception fault, params string[] args) {
        var handler = new FaultingHandler(fault);
        var time    = new FakeTimeProvider();
        var command = new SessionsCommand(
            Config.Root, Resolutions.At(Server, Config.Root), new RecordingCapacitorHttpClient(handler),
            new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory), time);

        using var capture = ConsoleOutput.StartErrorCapture();

        var running = command.HandleAsync(["sessions", .. args]);
        for (var i = 0; !running.IsCompleted && i < 1000; i++) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }

        var exit = await running.WaitAsync(TimeSpan.FromSeconds(10));

        return (exit, capture.GetCapturedError(), handler.Attempts);
    }

    static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "Connection refused (127.0.0.1:9)",
            new SocketException((int)SocketError.ConnectionRefused));

    [Test]
    public async Task A_refused_connection_fails_the_command() {
        var (exit, _, attempts) = await RunAsync(Refused(), "--repo", "acme/widgets");

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(attempts).IsGreaterThan(1);
    }

    [Test]
    public async Task A_refused_connection_says_refused_and_points_at_the_server_not_a_sign_in() {
        var (_, stderr, _) = await RunAsync(Refused(), "--repo", "acme/widgets");

        await Assert.That(stderr).Contains("kcap API cannot be reached at http://127.0.0.1:9 (connection refused)");
        await Assert.That(stderr).Contains("Start or restart the server");
        await Assert.That(stderr).Contains("Connection refused (127.0.0.1:9)");
        await Assert.That(stderr).DoesNotContain("kcap login");
    }

    /// <summary>The retry budget is 30s and each attempt is capped at 60s, so naming the cap beside the
    /// budget reads as a contradiction.</summary>
    [Test]
    public async Task The_line_names_the_budget_without_a_per_attempt_cap_longer_than_it() {
        var (_, stderr, _) = await RunAsync(Refused(), "--repo", "acme/widgets");

        await Assert.That(stderr).Contains("30s retry budget");
        await Assert.That(stderr).DoesNotContain("60s");
        await Assert.That(stderr).DoesNotContain("  ");
        await Assert.That(stderr.TrimEnd()).DoesNotContain("\r");
    }

    [Test]
    public async Task An_unresolvable_host_asks_for_the_url_and_the_network() {
        var fault = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (kcap.invalid:443)",
            new SocketException((int)SocketError.HostNotFound));

        var (exit, stderr, _) = await RunAsync(fault, "--repo", "acme/widgets");

        await Assert.That(exit).IsNotEqualTo(0);
        await Assert.That(stderr).Contains("(host name not found)");
        await Assert.That(stderr).Contains("Check the server URL and your network");
    }

    sealed class FaultingHandler(Exception fault) : HttpMessageHandler {
        int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref _attempts);

            return Task.FromException<HttpResponseMessage>(fault);
        }
    }
}
