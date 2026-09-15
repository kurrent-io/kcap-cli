using System.Net;
using System.Net.Sockets;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Asks the OS for a free ephemeral port and releases it again, because HttpListener will not
/// accept port 0 in a prefix. The port can be taken between the release and the bind, which is why
/// the caller retries rather than trusting the answer.
/// </summary>
public sealed class EphemeralLoopbackPortSource : ILoopbackPortSource {
    public static readonly EphemeralLoopbackPortSource Instance = new();

    public int Reserve() {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        try {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        } finally {
            probe.Stop();
        }
    }
}
