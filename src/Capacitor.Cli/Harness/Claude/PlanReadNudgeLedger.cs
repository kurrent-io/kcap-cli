using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Which documents a session was already nudged to declare. The server keeps no such record,
/// so without this every re-read of an undeclared plan would repeat the reminder. Best effort
/// throughout: a lost marker costs one repeated nudge, never the hook.</summary>
sealed class PlanReadNudgeLedger(ConfigRoot config) {
    public bool WasNudged(string sessionId, string path) {
        try { return File.Exists(Marker(sessionId, path)); } catch { return false; }
    }

    public void Record(string sessionId, string path) {
        try {
            var marker = Marker(sessionId, path);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, path);
        } catch { }
    }

    public void Evict(string sessionId) {
        try { Directory.Delete(SessionDir(sessionId), recursive: true); } catch { }
    }

    string SessionDir(string sessionId) => config.Path("plan-read", PolicySnapshotStore.Sanitize(sessionId));

    string Marker(string sessionId, string path) =>
        Path.Combine(SessionDir(sessionId), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32]);
}
