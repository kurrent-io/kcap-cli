using System.Net.Sockets;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>A connection that got no answer is told apart from one whose credential was refused,
/// through the wrapping the retry loop adds.</summary>
public class ConnectionFailuresTests {
    static HttpRequestException Spent(Exception last) => new("Request failed, and the 30s retry budget ran out.", last);

    [Test]
    public async Task A_refused_socket_under_the_retry_wrapper_is_refused() {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (localhost:5000)",
            new SocketException((int)SocketError.ConnectionRefused));

        await Assert.That(ConnectionFailures.Of(Spent(refused))).IsEqualTo(ConnectionFailure.Refused);
    }

    [Test]
    public async Task A_name_resolution_failure_is_named_as_one() {
        var dns = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");

        await Assert.That(ConnectionFailures.Of(Spent(dns))).IsEqualTo(ConnectionFailure.NameNotResolved);
        await Assert.That(ConnectionFailures.Of(new SocketException((int)SocketError.HostNotFound)))
            .IsEqualTo(ConnectionFailure.NameNotResolved);
    }

    /// <summary>A per-attempt cap fires as a cancellation; the HttpClient timeout as one wrapping a
    /// <see cref="TimeoutException"/>.</summary>
    [Test]
    public async Task A_cancelled_attempt_or_a_client_timeout_is_a_timeout() {
        await Assert.That(ConnectionFailures.Of(Spent(new TaskCanceledException()))).IsEqualTo(ConnectionFailure.TimedOut);
        await Assert.That(ConnectionFailures.Of(new TaskCanceledException("t", new TimeoutException())))
            .IsEqualTo(ConnectionFailure.TimedOut);
    }

    [Test]
    public async Task An_unrecognised_fault_is_other() {
        await Assert.That(ConnectionFailures.Of(new HttpRequestException("The SSL connection could not be established")))
            .IsEqualTo(ConnectionFailure.Other);
    }
}
