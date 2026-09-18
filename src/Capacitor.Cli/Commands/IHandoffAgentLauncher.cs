namespace Capacitor.Cli.Commands;

internal sealed record HandoffLaunchRequest(HandoffVendor Vendor, string Prompt, string ProfileName, string WorkingDirectory);

internal interface IHandoffAgentLauncher {
    /// <summary>Blocks until the agent exits.</summary>
    HandoffLaunchResult Launch(HandoffLaunchRequest request);
}
