using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit;

/// A hub attempt nothing answered is a server that is down or unreachable; any answer, a 401 or a 503 included,
/// is not, and a fault that is not a transport one is never guessed at.
public class LaneFailuresTests {
    [Test]
    public async Task A_refused_negotiate_is_unanswered_and_refused() {
        var ex = new HttpRequestException("No connection could be made", new SocketException((int)SocketError.ConnectionRefused));

        await Assert.That(LaneFailures.Unanswered(ex)).IsEqualTo(ConnectionFailure.Refused);
    }

    [Test]
    public async Task A_dropped_socket_is_unanswered() {
        await Assert.That(LaneFailures.Unanswered(new WebSocketException(WebSocketError.ConnectionClosedPrematurely)))
            .IsEqualTo(ConnectionFailure.Other);
    }

    [Test]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Forbidden)]
    public async Task A_status_code_is_an_answer(HttpStatusCode status) {
        var ex = new HttpRequestException("Response status code does not indicate success", null, status);

        await Assert.That(LaneFailures.Unanswered(ex)).IsNull();
    }

    [Test]
    public async Task A_non_transport_fault_or_none_is_not_classified() {
        await Assert.That(LaneFailures.Unanswered(new InvalidOperationException("handshake was canceled"))).IsNull();
        await Assert.That(LaneFailures.Unanswered(null)).IsNull();
    }
}
