namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Supplies a loopback port to bind. Injected rather than probed inline because the bind that
/// follows can lose the port to anything else on the machine, and only the source can say which
/// port the next attempt should try.
/// </summary>
public interface ILoopbackPortSource {
    int Reserve();
}
