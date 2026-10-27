using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness.Codex;

/// How many lines of a Codex session's rollout the server has acknowledged, as the root watcher last saw it.
/// The Codex hook declares only turns this count covers: the server holds at least those, and its own mark
/// can be no further. Only the root watcher writes or deletes it; a subagent's watcher shares the session id.
public sealed class WatchAckCursor(ConfigRoot config) {
    const string DirName = "watch";

    public string Path(string sessionId) =>
        config.Path(DirName, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..32] + ".acked");

    /// <returns>False when the count did not reach disk, so a caller tries again rather than assume it did.</returns>
    public bool Write(string sessionId, int lines) {
        try {
            var path = Path(sessionId);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, lines.ToString(CultureInfo.InvariantCulture));
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    public int? TryRead(string sessionId) => TryRead(sessionId, out _);

    /// <param name="missing">True only when no cursor file exists. A file that cannot be read is not missing:
    /// Windows refuses a reader while a new count is renamed into place.</param>
    public int? TryRead(string sessionId, out bool missing) {
        missing = false;
        try {
            return int.TryParse(File.ReadAllText(Path(sessionId)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var lines)
                ? lines
                : null;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            missing = true;
            return null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public void Delete(string sessionId) {
        try {
            File.Delete(Path(sessionId));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
