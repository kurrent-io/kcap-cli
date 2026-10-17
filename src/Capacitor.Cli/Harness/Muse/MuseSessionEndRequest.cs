namespace Capacitor.Cli.Harness.Muse;

/// <summary>
/// A session-end the Muse SessionEnd hook hands to the session's watcher: the hook's own
/// <c>/hooks/session-end/muse</c> payload, left where the watcher looks every tick. Under
/// <c>muse exec</c> the hook is killed well inside a second, before it could drain the log and post
/// the end itself; the watcher is a detached process, so it posts the end after its final drain.
/// </summary>
public sealed record MuseSessionEndRequest(string Payload) {
    public static string PathFor(WatcherPaths paths, string sessionId) =>
        Path.Combine(paths.Directory, $"{sessionId}.muse-end-requested");

    /// <summary>Written whole, then renamed into place, so a watcher never reads a partial request.</summary>
    public static void Write(WatcherPaths paths, string sessionId, string payload) {
        Directory.CreateDirectory(paths.Directory);
        var target = PathFor(paths, sessionId);
        var temp   = $"{target}.{Environment.ProcessId}.tmp";

        File.WriteAllText(temp, payload);
        File.Move(temp, target, overwrite: true);
    }

    public static MuseSessionEndRequest? TryRead(WatcherPaths paths, string sessionId) {
        try {
            var payload = File.ReadAllText(PathFor(paths, sessionId));
            return string.IsNullOrWhiteSpace(payload) ? null : new MuseSessionEndRequest(payload);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    public static void Delete(WatcherPaths paths, string sessionId) {
        try { File.Delete(PathFor(paths, sessionId)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
