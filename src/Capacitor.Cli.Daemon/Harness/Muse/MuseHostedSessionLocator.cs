using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Finds the session a hosted Muse agent runs from the marker its launch wrote, for the
/// daemon's own link when the hook's never lands.</summary>
internal static class MuseHostedSessionLocator {
    /// <summary>The marked session's canonical id and log; null until Muse has created the log, so a
    /// caller never records a path that does not exist.</summary>
    internal static (string SessionId, string Path)? TryLocate(ConfigRoot config, MusePaths paths, string agentId, DateOnly today) {
        var dir = config.Path("muse-hosted");
        if (!Directory.Exists(dir)) return null;

        IEnumerable<string> files;
        try {
            files = Directory.EnumerateFiles(dir, "*.json").ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        foreach (var file in files) {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;

            if (MuseHostedSessionMarker.TryRead(config, id.ToString("N")) is not { } marker
             || !string.Equals(marker.AgentId, agentId, StringComparison.Ordinal))
                continue;

            if (paths.ResolveSessionLog(id.ToString("D"), today) is { } log) return (id.ToString("N"), log);
        }

        return null;
    }
}
