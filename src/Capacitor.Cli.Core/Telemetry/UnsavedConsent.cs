namespace Capacitor.Cli.Core.Telemetry;

/// <summary>What usage data does on a machine where nobody saved a choice and no environment variable decides.</summary>
public enum UnsavedConsent {
    /// <summary>Sent, under the one-time notice: the cloud edition's opt-out.</summary>
    Share,

    /// <summary>Never sent: the local edition promises everything stays on the machine unless the user opts in.</summary>
    Withhold,

    /// <summary>A run that has not settled its edition yet, or is waiting for the local edition's answer: captures
    /// stay in memory, never sent or spooled, until the cloud edition is chosen or a choice is saved.</summary>
    Pending,
}
