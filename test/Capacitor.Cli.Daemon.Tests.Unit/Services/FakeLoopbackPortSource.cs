using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A port source whose first answer the test chooses, so a bind collision is arranged
/// rather than waited for. Later answers come from the OS, as production's would.</summary>
sealed class FakeLoopbackPortSource(int firstPort, Action? onReserve = null) : ILoopbackPortSource {
    public int Reservations { get; private set; }

    public int Reserve() {
        Reservations++;
        onReserve?.Invoke();

        return Reservations == 1 ? firstPort : EphemeralLoopbackPortSource.Instance.Reserve();
    }
}
