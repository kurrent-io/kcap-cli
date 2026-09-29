namespace Capacitor.Cli.Commands;

internal sealed record HandoffLaunchRequest(HandoffVendor Vendor, string Prompt, string ProfileName, string WorkingDirectory);
