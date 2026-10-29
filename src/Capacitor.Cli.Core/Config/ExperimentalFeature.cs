namespace Capacitor.Cli.Core.Config;

/// <param name="AppliesLive">True when a change takes effect for the next launch without a daemon restart.</param>
public sealed record ExperimentalFeature(string Id, string DisplayName, string Description, bool AppliesLive);
