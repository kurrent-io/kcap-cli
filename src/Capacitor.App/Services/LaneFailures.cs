using System.Net.Sockets;
using System.Net.WebSockets;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Services;

/// Whether a failed hub attempt reached a server at all. Only an attempt nothing answered is classified:
/// a status code, even a refusal, means the server is up, and a fault that is not a transport one says
/// nothing either way.
public static class LaneFailures {
    public static ConnectionFailure? Unanswered(Exception? exception) {
        var transport = false;

        for (var e = exception; e is not null; e = e.InnerException) {
            switch (e) {
                case HttpRequestException { StatusCode: not null }:
                case WebSocketException { WebSocketErrorCode: WebSocketError.NotAWebSocket }:
                    return null;
                case HttpRequestException or SocketException or WebSocketException or TimeoutException or IOException:
                    transport = true;

                    break;
            }
        }

        return transport ? ConnectionFailures.Of(exception!) : null;
    }
}
