using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

static class LocalPortPicker {
    public const int DefaultPort = 9473;

    const int Attempts = 20;

    /// <summary>The default port when it is free and not retired, otherwise an OS-assigned one that is free on both
    /// loopback families and not retired.</summary>
    public static int Choose(InstallRegistry registry, Func<int, bool>? isFree = null) {
        isFree ??= FreeOnLoopback;

        if (!registry.IsRetiredPort(DefaultPort) && isFree(DefaultPort)) return DefaultPort;

        for (var attempt = 0; attempt < Attempts; attempt++) {
            var port = AssignedPort();

            if (!registry.IsRetiredPort(port) && isFree(port)) return port;
        }

        throw new InvalidOperationException($"No free loopback port found after {Attempts} attempts.");
    }

    static int AssignedPort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static bool FreeOnLoopback(int port) => CanBind(IPAddress.Loopback, port) && (!Socket.OSSupportsIPv6 || CanBind(IPAddress.IPv6Loopback, port));

    static bool CanBind(IPAddress address, int port) {
        try {
            using var listener = new TcpListener(address, port);
            listener.Start();

            return true;
        }
        catch (SocketException) {
            return false;
        }
    }
}
