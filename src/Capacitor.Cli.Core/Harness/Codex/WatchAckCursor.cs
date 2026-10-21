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

    /// A failed write only costs the judge its window: the hook reads a missing or stale count as fewer turns.
    public void Write(string sessionId, int lines) {
        try {
            var path = Path(sessionId);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, lines.ToString(CultureInfo.InvariantCulture));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public int? TryRead(string sessionId) {
        try {
            return int.TryParse(File.ReadAllText(Path(sessionId)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var lines)
                ? lines
                : null;
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
