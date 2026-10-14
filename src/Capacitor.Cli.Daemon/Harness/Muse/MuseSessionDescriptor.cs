namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>The session a <c>muse serve</c> host started or resumed: its id and the log file Muse writes it to.</summary>
internal sealed record MuseSessionDescriptor(string SessionId, string LogPath, string? ModelId, string WorkspaceRoot);
