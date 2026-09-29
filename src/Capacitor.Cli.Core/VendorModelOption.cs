namespace Capacitor.Cli.Core;

/// One launchable model a daemon advertises for a vendor. Value is what the launch wire carries
/// (for Pi, `provider/id`); Label is what a picker shows.
public sealed record VendorModelOption(string Value, string Label);
