using System.Net.Sockets;

namespace Capacitor.Cli.Core.Http;

/// <summary>Why a request got no answer at all. A server that answered, even with a 401 or a 403, is
/// never one of these: only a credential problem is repaired by <c>kcap login</c>, and sending someone
/// there for a server that is down cannot help.</summary>
public enum ConnectionFailure {
    /// <summary>Nothing listens at the address: the server is stopped, or on another port.</summary>
    Refused,

    /// <summary>The host name did not resolve.</summary>
    NameNotResolved,

    /// <summary>No answer arrived in time.</summary>
    TimedOut,

    /// <summary>Any other transport fault, TLS included.</summary>
    Other,
}

public static class ConnectionFailures {
    /// <summary>Walks the whole chain: the retry loop wraps the last fault, and the socket error that
    /// names the cause sits under the handler's own exception.</summary>
    public static ConnectionFailure Of(Exception exception) {
        var cancelled = false;

        for (var e = exception; e is not null; e = e.InnerException) {
            switch (e) {
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return ConnectionFailure.Refused;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }:
                    return ConnectionFailure.NameNotResolved;
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                case TimeoutException:
                    return ConnectionFailure.TimedOut;
                case OperationCanceledException:
                    cancelled = true;

                    break;
            }
        }

        return cancelled ? ConnectionFailure.TimedOut : ConnectionFailure.Other;
    }

    extension(ConnectionFailure failure) {
        public string Reason => failure switch {
            ConnectionFailure.Refused         => "connection refused",
            ConnectionFailure.NameNotResolved => "host name not found",
            ConnectionFailure.TimedOut        => "no answer in time",
            _                                 => "connection failed",
        };

        /// <summary>What to do next. Never a sign-in: nothing here is a credential problem.</summary>
        public string Remedy => failure switch {
            ConnectionFailure.Refused         => "Start or restart the server, or retry once it is running.",
            ConnectionFailure.NameNotResolved => "Check the server URL and your network, then retry.",
            ConnectionFailure.TimedOut        => "Retry; if the server stays silent, restart it.",
            _                                 => "Check that the server is running and the URL is right, then retry.",
        };
    }
}
