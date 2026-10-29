namespace Capacitor.Cli.Service.Sessions;

/// <summary>The user signed in to a session, as the token Windows holds for it describes them</summary>
public sealed record SessionUser(int SessionId, string Sid, string ProfileDirectory);
