using System.Net.Sockets;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A daemon control socket that hangs up on every client and remembers that one came, so a
/// test can prove the command never reached for the daemon. Hanging up, not ignoring, keeps a command
/// that does dial from blocking the suite on a reply that never arrives.</summary>
sealed class ControlSocketTripwire : IDisposable {
    readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    int _dials;

    public ControlSocketTripwire(DaemonStore store, string name) {
        store.EnsureDirectory();
        _listener.Bind(new UnixDomainSocketEndPoint(store.SocketPath(name)));
        _listener.Listen(8);
        _ = Task.Run(HangUpOnEveryDial);
    }

    // A connection still queued behind the accept loop counts too.
    public bool WasDialed => Volatile.Read(ref _dials) > 0 || _listener.Poll(0, SelectMode.SelectRead);

    async Task HangUpOnEveryDial() {
        try {
            while (true) {
                using var connection = await _listener.AcceptAsync();
                Interlocked.Increment(ref _dials);
            }
        } catch (Exception ex) when (ex is ObjectDisposedException or SocketException) {
            // The test is over.
        }
    }

    public void Dispose() => _listener.Dispose();
}
