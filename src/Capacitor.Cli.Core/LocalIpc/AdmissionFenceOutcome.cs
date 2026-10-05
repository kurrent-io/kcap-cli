namespace Capacitor.Cli.Core.LocalIpc;

public enum AdmissionFenceOutcome {
    Acquired,
    /// The daemon is running or admitting work.
    Busy,
    /// The daemon answers but does not advertise the fence.
    Unsupported,
    /// No usable answer: unreachable, timed out, refused or malformed.
    Unavailable,
}
