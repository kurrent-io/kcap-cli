namespace Capacitor.Cli.Commands;

internal interface IHandoffAgentLauncher {
    /// <summary>Blocks until the agent exits.</summary>
    HandoffLaunchResult Launch(HandoffLaunchRequest request);
}
