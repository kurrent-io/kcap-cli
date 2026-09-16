using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A starter a test hands to the code under test. Every spawn here sits in a catch-all that
/// swallows both outcomes, so the start count is what separates "the guard refused" from "the
/// start failed" — asserting zero is the only proof a guard ran.
/// </summary>
sealed class FakeProcessStarter : IProcessStarter {
    readonly Func<ProcessStartInfo, Process?> _behaviour;

    FakeProcessStarter(Func<ProcessStartInfo, Process?> behaviour) => _behaviour = behaviour;

    /// <summary>Fails the way a start that found nothing to run does: null, no exception.</summary>
    public static FakeProcessStarter Refusing()               => new(_ => null);
    public static FakeProcessStarter Throwing(Exception boom) => new(_ => throw boom);

    /// <summary>Stands a real child in for the one the code under test asked for.</summary>
    public static FakeProcessStarter Running(Func<ProcessStartInfo, Process?> stub) => new(stub);

    public ProcessStartInfo? Seen   { get; private set; }
    public int               Starts { get; private set; }

    public Process? Start(ProcessStartInfo psi) {
        Seen = psi;
        Starts++;

        return _behaviour(psi);
    }

    /// <summary>Counts alongside <see cref="Start"/>: a test asserting a guard ran cares that
    /// nothing was spawned, not which spawn shape the caller reached for.</summary>
    public int? StartDetached(ProcessStartInfo psi) => Start(psi)?.Id;
}
