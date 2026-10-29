namespace Capacitor.Cli.Daemon.Services;

/// <summary>How a ledgered agent came back after a daemon restart.</summary>
internal enum ResumeOutcome {
    /// <summary>Its vendor session was resumed under the same agent id.</summary>
    Resumed,

    /// <summary>A new agent continued its work through kcap continue.</summary>
    Continued,

    /// <summary>Neither ran; the entry is left interrupted.</summary>
    Interrupted,

    /// <summary>A stop ended the resumed agent before its resume was judged.</summary>
    Stopped
}
