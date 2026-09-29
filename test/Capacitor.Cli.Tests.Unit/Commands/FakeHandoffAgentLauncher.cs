using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeHandoffAgentLauncher(HandoffLaunchResult result) : IHandoffAgentLauncher {
    public HandoffLaunchRequest? Seen { get; private set; }
    public int Launches { get; private set; }

    public static FakeHandoffAgentLauncher Ran()     => new(new(HandoffLaunchStatus.Ran, 0, null));
    public static FakeHandoffAgentLauncher Failing() => new(new(HandoffLaunchStatus.LaunchFailed, 127, "not found"));

    public HandoffLaunchResult Launch(HandoffLaunchRequest request) { Seen = request; Launches++; return result; }
}
