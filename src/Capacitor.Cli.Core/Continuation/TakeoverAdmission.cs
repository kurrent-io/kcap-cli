namespace Capacitor.Cli.Core.Continuation;

/// <summary>What admits a source to a takeover. <see cref="EndedOnly"/> never forces and never treats a stale
/// active session as gone: only the server's own <c>ended</c> admits it.</summary>
public enum TakeoverAdmission {
    /// <summary>Ended, a local exit record, or an active session idle past the server's stale threshold.</summary>
    Judged,
    Forced,
    EndedOnly,
}
