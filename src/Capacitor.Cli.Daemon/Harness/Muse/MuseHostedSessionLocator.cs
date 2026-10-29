using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Finds the session a hosted Muse agent runs, for the daemon's own link when the hook's never
/// lands: the session its runtime opened, else the marker its launch wrote.</summary>
internal static class MuseHostedSessionLocator {
    /// <summary>The session's canonical id and log; null until Muse has created the log, so a caller never
    /// records a path that does not exist. The runtime's session comes first because a session that has
    /// already ended has had its marker deleted by the end hook.</summary>
    internal static (string SessionId, string Path)? TryLocate(
            UserHome home, MusePaths paths, string agentId, DateOnly today, MuseSessionDescriptor? opened) {
        if (opened is not null && Guid.TryParse(opened.SessionId, out var openedId) && File.Exists(opened.LogPath))
            return (openedId.ToString("N"), opened.LogPath);

        var dir = MuseHostedSessionMarker.DirectoryFor(home);
        if (!Directory.Exists(dir)) return null;

        IEnumerable<string> files;
        try {
            files = Directory.EnumerateFiles(dir, "*.json").ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        foreach (var file in files) {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;

            if (MuseHostedSessionMarker.TryRead(home, id.ToString("N")) is not { } marker
             || !string.Equals(marker.AgentId, agentId, StringComparison.Ordinal))
                continue;

            if (paths.ResolveSessionLog(id.ToString("D"), today) is { } log) return (id.ToString("N"), log);
        }

        return null;
    }
}
