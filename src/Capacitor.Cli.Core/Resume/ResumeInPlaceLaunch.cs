namespace Capacitor.Cli.Core.Resume;

/// <summary>An in-place resume of an ended session. <paramref name="ReservationToken"/> is the source's lease, held by
/// this daemon's connection, renewed until the session is active again and then ended. <paramref name="AccountId"/>
/// and <paramref name="ConfigDir"/> are the store the session was recorded under.</summary>
public sealed record ResumeInPlaceLaunch(
    string  SourceSessionId,
    string  Directory,
    string  ReservationToken,
    string? AccountId,
    string  ConfigDir
);
